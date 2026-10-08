using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed record TohRoleData(int RoleId, string? RoleName, bool? IsNeutralKiller,
    bool? IsKiller, bool? OpportunistCanKill = null, string? CustomRoleType = null);

internal sealed class TohLiveRoleReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private int pid = -1;
    private string session = string.Empty;
    private TohLayout? layout;
    private Task<TohDiscovery>? discoveryTask;
    private DateTimeOffset retryAt;
    private bool needsCanKill;
    private bool needsKiller;

    public string Status { get; private set; } = "TOH4E役職未取得";
    public IReadOnlyList<TohRoleDefinition> RoleCatalog => layout?.RoleCatalog ?? [];

    public void Reset()
    {
        pid = -1;
        session = string.Empty;
        layout = null;
        discoveryTask = null;
        retryAt = default;
        needsCanKill = false;
        needsKiller = false;
        Status = "TOH4E役職未取得";
    }

    public IReadOnlyDictionary<int, TohRoleData> Update(int processId, string round,
        Func<long, int, byte[]> read)
    {
        if (pid != processId)
        {
            Reset();
            pid = processId;
        }
        if (session != round)
        {
            session = round;
            retryAt = default;
        }
        if (discoveryTask is { IsCompleted: true })
        {
            try
            {
                var discovery = discoveryTask.GetAwaiter().GetResult();
                discovery.Layout.Validate(processId);
                layout = discovery.Layout;
                retryAt = DateTimeOffset.UtcNow.AddSeconds(3);
            }
            catch (Exception error)
            {
                retryAt = DateTimeOffset.UtcNow.AddSeconds(30);
                Status = $"TOH4E未取得: {error.Message}";
            }
            discoveryTask = null;
        }
        if ((layout is null || layout.RoleCatalog.Count == 0 || needsCanKill || needsKiller) && discoveryTask is null &&
            DateTimeOffset.UtcNow >= retryAt)
        {
            discoveryTask = DiscoverAsync(processId);
            retryAt = DateTimeOffset.UtcNow.AddSeconds(30);
            Status = "TOH4Eの役職読み取り位置を確認中…";
        }
        if (layout is null) return new Dictionary<int, TohRoleData>();
        try
        {
            var roles = ReadRoles(layout, read);
            needsCanKill = layout.OpportunistCanKillSlot == 0 &&
                roles.Values.Any(role => role.RoleName == "Opportunist");
            needsKiller = layout.KillerLayout is null || roles.Values.Any(role => role.IsKiller is null);
            Status = $"TOH4E役職を自動更新中（{roles.Count}人）／取得元: ローカルMODのPlayerState";
            return roles;
        }
        catch
        {
            needsKiller = true;
            Status = "TOH4E役職未取得（配列・役職の更新待ち）";
            return new Dictionary<int, TohRoleData>();
        }
    }

    private static async Task<TohDiscovery> DiscoverAsync(int processId)
    {
        var helper = SnrLiveRoleReader.FindHelper() ??
            throw new FileNotFoundException("TOH4E役職読み取りツールが見つかりません");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(helper)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("--toh");
        if (!process.Start()) throw new InvalidOperationException("TOH4E読み取りツールを起動できません");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var response = JsonSerializer.Deserialize<TohDiscovery>(await outputTask, JsonOptions);
            var error = await errorTask;
            if (process.ExitCode != 0 || response?.Status != "ok" || response.Pid != processId ||
                response.Layout is null)
                throw new InvalidOperationException(response?.Message ?? error.Trim());
            return response;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("TOH4E読み取りツールが時間内に応答しませんでした");
        }
    }

    internal static IReadOnlyDictionary<int, TohRoleData> ReadRoles(TohLayout layout,
        Func<long, int, byte[]> read)
    {
        var first = ReadSnapshot(layout, read);
        var second = ReadSnapshot(layout, read);
        if (first.Count != second.Count || first.Any(pair =>
            !second.TryGetValue(pair.Key, out var role) || role != pair.Value))
            throw new InvalidDataException("TOH4E roles changed during read");
        return second;
    }

    private static Dictionary<int, TohRoleData> ReadSnapshot(TohLayout layout,
        Func<long, int, byte[]> read)
    {
        ulong U64(ulong address) => BitConverter.ToUInt64(read(checked((long)address), 8));
        var killers = new Dictionary<int, (ulong State, bool? IsKiller)>();
        DictionaryImage? killerImage = null;
        if (layout.KillerLayout is { } killerLayout)
        {
            killerImage = ReadDictionary(killerLayout, read);
            foreach (var entry in Enumerate(killerImage, killerLayout))
            {
                var role = entry.Value;
                if (!TohLayout.ValidPointer(role) || killers.ContainsKey(entry.Id))
                    throw new InvalidDataException("Invalid TOH4E active role");
                var type = killerLayout.Types.GetValueOrDefault(U64(role).ToString(CultureInfo.InvariantCulture));
                killers.Add(entry.Id, (type is null ? 0 : U64(role + (ulong)type.StateOffset), type?.IsKiller));
            }
        }

        var image = ReadDictionary(layout, read);
        bool? canKill = null;
        if (layout.OpportunistCanKillSlot != 0)
        {
            var value = read(checked((long)layout.OpportunistCanKillSlot), 1)[0];
            if (value > 1) throw new InvalidDataException("Invalid TOH4E CanKill");
            canKill = value == 1;
        }
        var result = new Dictionary<int, TohRoleData>();
        foreach (var entry in Enumerate(image, layout))
        {
            var player = entry.Value;
            if (!TohLayout.ValidPointer(player) || U64(player) != layout.PlayerType ||
                read(checked((long)(player + (ulong)layout.IdOffset)), 1)[0] != entry.Id || result.ContainsKey(entry.Id))
                throw new InvalidDataException("TOH4E player identity changed");
            var roleId = BitConverter.ToInt32(read(checked((long)(player + (ulong)layout.RoleOffset)), 4));
            layout.Names.TryGetValue(roleId.ToString(CultureInfo.InvariantCulture), out var name);
            var neutralKiller = NeutralKiller(name, canKill);
            var faction = layout.RoleCatalog.FirstOrDefault(role => role.RoleId == roleId)?.CustomRoleType;
            var killer = name is not null and not "NotAssigned" &&
                killers.TryGetValue(entry.Id, out var activeRole) && activeRole.State == player
                ? activeRole.IsKiller : null;
            result.Add(entry.Id, new TohRoleData(roleId, name, neutralKiller, killer,
                name == "Opportunist" ? canKill : null, faction));
        }
        ValidateDictionary(image, layout, read);
        if (killerImage is not null) ValidateDictionary(killerImage, layout.KillerLayout!, read);
        return result;
    }

    internal static bool? NeutralKiller(string? name, bool? opportunistCanKill)
    {
        if (string.IsNullOrEmpty(name) || name == "NotAssigned") return null;
        if (name == "Opportunist") return opportunistCanKill;
        return name is "Egoist" or "Jackal" or "Gizoku" or "Oniichan" or "DarkHide";
    }

    private static DictionaryImage ReadDictionary(TohDictionaryLayout layout,
        Func<long, int, byte[]> read)
    {
        ulong U64(ulong address) => BitConverter.ToUInt64(read(checked((long)address), 8));
        int I32(ulong address) => BitConverter.ToInt32(read(checked((long)address), 4));
        var dictionary = U64(layout.DictionarySlot);
        if (!TohLayout.ValidPointer(dictionary) || U64(dictionary) != layout.DictionaryType)
            throw new InvalidDataException("TOH4E dictionary unavailable");
        var version = I32(dictionary + (ulong)layout.VersionOffset);
        var entries = U64(dictionary + (ulong)layout.EntriesOffset);
        var count = I32(dictionary + (ulong)layout.CountOffset);
        if (!TohLayout.ValidPointer(entries) || U64(entries) != layout.EntriesType ||
            count is < 0 or > 256 || count > I32(entries + 8))
            throw new InvalidDataException("Invalid TOH4E entries");
        var bytes = count == 0 ? [] : read(checked((long)(entries + (ulong)layout.DataOffset)),
            checked(count * layout.Stride));
        return new DictionaryImage(dictionary, version, entries, count, bytes);
    }

    private static IEnumerable<(int Id, ulong Value)> Enumerate(DictionaryImage image,
        TohDictionaryLayout layout)
    {
        for (var i = 0; i < image.Count; i++)
        {
            var start = checked((int)i * layout.Stride);
            if (BitConverter.ToInt32(image.Bytes, start + layout.NextOffset) < -1) continue;
            yield return (image.Bytes[start + layout.KeyOffset],
                BitConverter.ToUInt64(image.Bytes, start + layout.ValueOffset));
        }
    }

    private static void ValidateDictionary(DictionaryImage image, TohDictionaryLayout layout,
        Func<long, int, byte[]> read)
    {
        ulong U64(ulong address) => BitConverter.ToUInt64(read(checked((long)address), 8));
        int I32(ulong address) => BitConverter.ToInt32(read(checked((long)address), 4));
        if (U64(layout.DictionarySlot) != image.Dictionary ||
            U64(image.Dictionary) != layout.DictionaryType ||
            I32(image.Dictionary + (ulong)layout.VersionOffset) != image.Version ||
            U64(image.Dictionary + (ulong)layout.EntriesOffset) != image.Entries ||
            I32(image.Dictionary + (ulong)layout.CountOffset) != image.Count ||
            U64(image.Entries) != layout.EntriesType ||
            (image.Bytes.Length > 0 && !read(checked((long)(image.Entries + (ulong)layout.DataOffset)), image.Bytes.Length)
                .AsSpan().SequenceEqual(image.Bytes)))
            throw new InvalidDataException("TOH4E dictionary changed");
    }

    private sealed record DictionaryImage(ulong Dictionary, int Version, ulong Entries,
        int Count, byte[] Bytes);

    private sealed class TohDiscovery
    {
        public string Status { get; set; } = string.Empty;
        public int Pid { get; set; }
        public string? Message { get; set; }
        public TohLayout Layout { get; set; } = new();
    }
}

internal class TohDictionaryLayout
{
    public ulong DictionarySlot { get; set; }
    public ulong DictionaryType { get; set; }
    public ulong EntriesType { get; set; }
    public int EntriesOffset { get; set; }
    public int CountOffset { get; set; }
    public int VersionOffset { get; set; }
    public int DataOffset { get; set; }
    public int Stride { get; set; }
    public int NextOffset { get; set; }
    public int KeyOffset { get; set; }
    public int ValueOffset { get; set; }

    protected void ValidateDictionary()
    {
        if (!TohLayout.ValidPointer(DictionarySlot) || !TohLayout.ValidPointer(DictionaryType) ||
            !TohLayout.ValidPointer(EntriesType) || EntriesOffset is < 8 or > 1024 ||
            CountOffset is < 8 or > 1024 || VersionOffset is < 8 or > 1024 ||
            DataOffset != 16 || Stride is < 16 or > 64 ||
            NextOffset < 0 || NextOffset + 4 > Stride ||
            KeyOffset < 0 || KeyOffset + 1 > Stride ||
            ValueOffset < 0 || ValueOffset + 8 > Stride)
            throw new InvalidDataException("Invalid TOH4E dictionary layout");
    }
}

internal sealed class TohLayout : TohDictionaryLayout
{
    public int Pid { get; set; }
    public int PointerSize { get; set; }
    public ulong PlayerType { get; set; }
    public int IdOffset { get; set; }
    public int RoleOffset { get; set; }
    public ulong OpportunistCanKillSlot { get; set; }
    public Dictionary<string, string> Names { get; set; } = [];
    public List<TohRoleDefinition> RoleCatalog { get; set; } = [];
    public TohKillerLayout? KillerLayout { get; set; }

    public static bool ValidPointer(ulong pointer) =>
        pointer >= 0x10000 && pointer <= long.MaxValue && pointer % 8 == 0;

    public void Validate(int expectedPid)
    {
        ValidateDictionary();
        if (Pid != expectedPid || PointerSize != 8 || !ValidPointer(PlayerType) ||
            IdOffset is < 8 or > 1024 || RoleOffset is < 8 or > 1024 ||
            OpportunistCanKillSlot != 0 &&
            (OpportunistCanKillSlot < 0x10000 || OpportunistCanKillSlot > long.MaxValue))
            throw new InvalidDataException("Invalid TOH4E live layout");
        if (!TohRoleCatalog.IsValid(RoleCatalog) || RoleCatalog.Any(role =>
            !Names.TryGetValue(role.RoleId.ToString(CultureInfo.InvariantCulture), out var name) ||
            name != role.RoleName))
            throw new InvalidDataException("Invalid TOH4E role catalog");
        KillerLayout?.Validate();
    }
}

internal sealed class TohKillerLayout : TohDictionaryLayout
{
    public Dictionary<string, TohKillerType> Types { get; set; } = [];

    public void Validate()
    {
        ValidateDictionary();
        foreach (var (key, type) in Types)
        {
            if (!ulong.TryParse(key, out var pointer) || !TohLayout.ValidPointer(pointer) ||
                type.StateOffset is < 8 or > 1024)
                throw new InvalidDataException("Invalid TOH4E killer layout");
        }
    }
}

internal sealed class TohKillerType
{
    public bool IsKiller { get; set; }
    public int StateOffset { get; set; }
}
