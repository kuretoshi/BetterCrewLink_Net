using System.Text;

namespace TanukiBCL.VoiceProbe;

/// <summary>Port of TanukiBCL 3.2.9 common/appVersion.ts and VoiceController.updateVersionWarning.</summary>
public static partial class AppVersionPolicy
{
    private const long MaxSafeInteger = 9_007_199_254_740_991L;

    /// <summary>Compare release versions numerically; null for malformed peer input.</summary>
    public static int? Compare(string left, string right)
    {
        Span<long> a = stackalloc long[3];
        Span<long> b = stackalloc long[3];
        if (!TryParse(left, a) || !TryParse(right, b)) return null;
        for (var i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
        return 0;
    }

    private static bool TryParse(string value, Span<long> parts)
    {
        if (value.Length is 0 or > 32) return false;
        var component = 0;
        var number = 0L;
        var hasDigit = false;
        foreach (var character in value)
        {
            if (character == '.')
            {
                if (!hasDigit || component >= 2) return false;
                parts[component++] = number;
                number = 0;
                hasDigit = false;
                continue;
            }
            if (character is < '0' or > '9') return false;
            var digit = character - '0';
            if (number > (MaxSafeInteger - digit) / 10) return false;
            number = number * 10 + digit;
            hasDigit = true;
        }
        if (!hasDigit || component != 2) return false;
        parts[2] = number;
        return true;
    }

    /// <summary>The wire version a .NET build reports: its official compatibility release.</summary>
    public static string WireVersion(string informationalVersion) =>
        informationalVersion.Split('-', '+')[0];

    /// <summary>Notify only the older side of a comparison with the lobby host.</summary>
    public static string? RequiredVersion(string local, string? host, bool isHost,
        IReadOnlyList<string> participants)
    {
        IEnumerable<string> candidates = isHost ? participants : host is null ? [] : [host];
        return candidates
            .Where(version => Compare(local, version) == -1)
            .OrderByDescending(version => version, Comparer<string>.Create((a, b) => Compare(a, b) ?? 0))
            .FirstOrDefault();
    }

    /// <summary>Known peers with a different valid release version from this client.</summary>
    public static IReadOnlyList<(string Name, string Version)> Mismatched(string local,
        IEnumerable<(string Name, string Version)> peers) =>
        peers.Where(peer => Compare(local, peer.Version) is { } comparison && comparison != 0).ToArray();

    public static string Warning(string local, string? hostVersion, bool isHost,
        IReadOnlyList<(string Name, string Version)> participants)
    {
        var required = !isHost && hostVersion is not null && Compare(local, hostVersion) == -1
            ? hostVersion : null;
        StringBuilder? mismatch = null;
        for (var i = 0; i < participants.Count; i++)
        {
            var (name, version) = participants[i];
            var comparison = Compare(local, version);
            if (comparison is null) continue;
            if (comparison != 0)
            {
                if (mismatch is null)
                    mismatch = new StringBuilder("TanukiBCLのバージョンが異なるプレイヤーがいます: ");
                else mismatch.Append('、');
                mismatch.Append(name).Append("（v").Append(version).Append('）');
            }
            if (isHost && comparison == -1 &&
                (required is null || Compare(version, required) == 1)) required = version;
        }
        var mismatchWarning = mismatch is null ? string.Empty : mismatch
            .Append("。この端末はv").Append(local).Append("です。").ToString();
        var updateWarning = required is null ? string.Empty : isHost
            ? $"参加者はv{required}です。ホストのTanukiBCLをアップデートしてください（現在v{local}）。"
            : $"ホストはv{required}です。この端末のTanukiBCLをアップデートしてください（現在v{local}）。";
        if (mismatchWarning.Length == 0) return updateWarning;
        return updateWarning.Length == 0 ? mismatchWarning : $"{mismatchWarning} {updateWarning}";
    }

    internal static void Verify()
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        Require(Compare("3.2.9", "3.2.10") == -1 && Compare("3.10.0", "3.9.9") == 1 &&
            Compare("3.2.9", "3.2.9") == 0, "Numeric release comparison differs from 3.2.9");
        Require(Compare("3.2.9-net-beta.1", "3.2.9") is null && Compare("3.2", "3.2.9") is null &&
            Compare("3.2.99999999999", "3.2.9") == 1 && Compare("3.2.9007199254740992", "3.2.9") is null &&
            Compare("3.2.١", "3.2.9") is null && Compare("3.2." + new string('1', 28), "3.2.9") is null,
            "Malformed peer versions were accepted");
        Require(Compare("0003.02.9", "3.2.9") == 0 &&
            Compare("9007199254740991.0.0", "9007199254740990.0.0") == 1 &&
            Compare("3..9", "3.2.9") is null && Compare("3.2.9.", "3.2.9") is null &&
            Compare("3.2.9 ", "3.2.9") is null,
            "Version parser changed the release grammar or safe-integer boundary");
        Require(WireVersion("3.2.9-net-beta.1") == "3.2.9" && WireVersion("3.2.9+abc") == "3.2.9",
            ".NET wire version is not the official compatibility release");
        Require(RequiredVersion("3.2.8", "3.2.9", false, []) == "3.2.9" &&
            RequiredVersion("3.2.9", "3.2.8", false, []) is null &&
            RequiredVersion("3.2.8", null, true, ["3.2.9", "3.2.10", "3.2.7"]) == "3.2.10",
            "Only the older side must be notified");
        var hostWarning = Warning("3.2.9", "3.2.9", true, [("A", "3.2.10"), ("B", "3.2.9")]);
        Require(hostWarning == "TanukiBCLのバージョンが異なるプレイヤーがいます: A（v3.2.10）。この端末はv3.2.9です。 " +
            "参加者はv3.2.10です。ホストのTanukiBCLをアップデートしてください（現在v3.2.9）。",
            "Host version warning differs from 3.2.9");
        Require(Warning("3.2.10", "3.2.9", false, [("Host", "3.2.9")]) ==
            "TanukiBCLのバージョンが異なるプレイヤーがいます: Host（v3.2.9）。この端末はv3.2.10です。",
            "Newer participant must see the mismatch without an update request");
        Require(Warning("3.2.9", "3.2.9", true, [("A", "3.2.10"), ("B", "3.2.11")]) ==
            "TanukiBCLのバージョンが異なるプレイヤーがいます: A（v3.2.10）、B（v3.2.11）。この端末はv3.2.9です。 " +
            "参加者はv3.2.11です。ホストのTanukiBCLをアップデートしてください（現在v3.2.9）。",
            "Multiple version mismatches changed order or maximum required version");
        Require(Warning("3.2.9", null, false, [("A", "bad")]) == string.Empty, "Malformed peer version produced a warning");
        var comparisonTotal = 0;
        for (var i = 0; i < 100; i++) comparisonTotal += Compare("3.2.27", "3.2.28") ?? 0;
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) comparisonTotal += Compare("3.2.27", "3.2.28") ?? 0;
        var comparisonAllocation = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Require(comparisonTotal == -10_100 && comparisonAllocation == 0,
            "Repeated release comparisons should not allocate managed objects");
        Console.WriteLine($"App-version comparisons: {comparisonAllocation:N0} B / 10,000 calls");
        IReadOnlyList<(string Name, string Version)> matchingParticipants = [("Peer", "3.2.27")];
        for (var i = 0; i < 100; i++) _ = Warning("3.2.27", "3.2.27", true, matchingParticipants);
        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            Require(Warning("3.2.27", "3.2.27", true, matchingParticipants).Length == 0,
                "Equal peer versions produced a warning");
        var warningAllocation = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Require(warningAllocation == 0, "Unchanged matching app versions should not allocate warning data");
        Console.WriteLine($"Matching app-version warnings: {warningAllocation:N0} B / 10,000 calls");
        Console.WriteLine("[PASS] 3.2.9 app-version comparison, older-side update notice and mismatch list");
    }

}
