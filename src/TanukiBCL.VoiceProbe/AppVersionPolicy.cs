using System.Globalization;
using System.Text.RegularExpressions;

namespace TanukiBCL.VoiceProbe;

/// <summary>Port of TanukiBCL 3.2.9 common/appVersion.ts and VoiceController.updateVersionWarning.</summary>
public static partial class AppVersionPolicy
{
    /// <summary>Compare release versions numerically; null for malformed peer input.</summary>
    public static int? Compare(string left, string right)
    {
        static long[]? Parse(string value)
        {
            if (value.Length > 32 || !ReleasePattern().IsMatch(value)) return null;
            var parts = new long[3];
            var text = value.Split('.');
            for (var i = 0; i < 3; i++)
            {
                // Number.isSafeInteger: parts above 2^53 - 1 make the version malformed.
                if (!long.TryParse(text[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]) ||
                    parts[i] > 9_007_199_254_740_991L)
                    return null;
            }
            return parts;
        }
        var a = Parse(left);
        var b = Parse(right);
        if (a is null || b is null) return null;
        for (var i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
        return 0;
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
        var required = RequiredVersion(local, hostVersion, isHost,
            participants.Select(player => player.Version).ToArray());
        var mismatches = Mismatched(local, participants);
        var mismatchWarning = mismatches.Count > 0
            ? $"TanukiBCLのバージョンが異なるプレイヤーがいます: {string.Join("、",
                mismatches.Select(player => $"{player.Name}（v{player.Version}）"))}。この端末はv{local}です。"
            : string.Empty;
        var updateWarning = required is null ? string.Empty : isHost
            ? $"参加者はv{required}です。ホストのTanukiBCLをアップデートしてください（現在v{local}）。"
            : $"ホストはv{required}です。この端末のTanukiBCLをアップデートしてください（現在v{local}）。";
        return string.Join(" ", new[] { mismatchWarning, updateWarning }.Where(text => text.Length > 0));
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
        Require(Warning("3.2.9", null, false, [("A", "bad")]) == string.Empty, "Malformed peer version produced a warning");
        Console.WriteLine("[PASS] 3.2.9 app-version comparison, older-side update notice and mismatch list");
    }

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleasePattern();
}
