using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed record SnrRoleData(int RoleId, string? RoleName, int? ModifierId,
    string? ModifierName, int? GhostRoleId, string? GhostRoleName,
    bool? IsNeutral = null, bool? CanKill = null, double? JumboCurrentSize = null,
    double? JumboMaxSize = null, string? Hat2Id = null, string? Visor2Id = null)
{
    public bool IsJackal => RoleName is "Jackal" or "WaveCannonJackal";
    public bool IsSidekick => RoleName is "Sidekick" or "SidekickWaveCannon";
    public bool IsJackalTeam => IsJackal || IsSidekick;
    public bool IsNeutralKiller => IsJackal || IsNeutral == true && CanKill == true;
    public bool HasJumbo => ModifierName?.Split(" | ").Contains("JumboModifier") == true;
}

internal sealed class SnrLiveRoleReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private int pid = -1;
    private string session = string.Empty;
    private SnrLiveLayout? layout;
    private Task<SnrDiscovery>? discoveryTask;
    private DateTimeOffset nextDiscoveryAt;
    private Dictionary<int, SnrHelperPlayer> metadata = [];

    public string Status { get; private set; } = "SNR役職未取得";

    public void Reset()
    {
        pid = -1;
        session = string.Empty;
        layout = null;
        discoveryTask = null;
        nextDiscoveryAt = default;
        metadata = [];
        Status = "SNR役職未取得";
    }

    public IReadOnlyDictionary<int, SnrRoleData> Update(int processId, string round,
        Func<long, int, byte[]> read)
    {
        if (pid != processId || session != round)
        {
            Reset();
            pid = processId;
            session = round;
        }

        if (discoveryTask is { IsCompleted: true })
        {
            try
            {
                var discovery = discoveryTask.GetAwaiter().GetResult();
                discovery.LiveLayout.Validate(processId);
                layout = discovery.LiveLayout;
                metadata = discovery.Players.Where(player => player.PlayerId is >= 0 and < 256 &&
                        player.Role?.Value is >= 0 and <= int.MaxValue)
                    .ToDictionary(player => player.PlayerId, player => player);
                nextDiscoveryAt = DateTimeOffset.UtcNow.AddSeconds(5);
                Status = "SNR役職を自動更新中";
            }
            catch (Exception error)
            {
                nextDiscoveryAt = DateTimeOffset.UtcNow.AddSeconds(5);
                Status = $"SNR役職取得待機中: {error.Message}";
            }
            discoveryTask = null;
        }

        if (discoveryTask is null && DateTimeOffset.UtcNow >= nextDiscoveryAt)
        {
            discoveryTask = DiscoverAsync(processId);
            nextDiscoveryAt = DateTimeOffset.UtcNow.AddSeconds(5);
        }
        if (layout is null) return new Dictionary<int, SnrRoleData>();

        try
        {
            var roles = ReadRoles(layout, read);
            var result = new Dictionary<int, SnrRoleData>(roles.Count);
            var needMetadata = false;
            var needJumboLayout = false;
            foreach (var (id, role) in roles)
            {
                if (metadata.TryGetValue(id, out var detail) && detail.Role?.Value == role.RoleId)
                {
                    result[id] = role with
                    {
                        IsNeutral = role.IsJackal ? true : detail.IsNeutral,
                        CanKill = role.IsJackal ? true : detail.CanKill,
                        Hat2Id = detail.Hat2Id,
                        Visor2Id = detail.Visor2Id
                    };
                }
                else
                {
                    result[id] = role.IsJackal ? role with { IsNeutral = true, CanKill = true } : role;
                    needMetadata |= !role.IsJackal;
                }
                needJumboLayout |= role.HasJumbo && layout.Jumbo is null;
            }
            if (needMetadata || needJumboLayout)
                nextDiscoveryAt = DateTimeOffset.UtcNow.AddSeconds(1);
            Status = "SNR役職を自動更新中";
            return result;
        }
        catch
        {
            // Managed objects may move between unsuspended reads; never reuse
            // old role values after an inconsistent sample.
            Status = "SNR役職の更新待ち";
            return new Dictionary<int, SnrRoleData>();
        }
    }

    private static async Task<SnrDiscovery> DiscoverAsync(int processId)
    {
        var helper = FindHelper() ?? throw new FileNotFoundException("SNR役職読み取りツールが見つかりません");
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
        if (!process.Start()) throw new InvalidOperationException("SNR読み取りツールを起動できません");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            var response = JsonSerializer.Deserialize<SnrDiscovery>(output, JsonOptions);
            if (process.ExitCode != 0 || response?.Status != "ok" || response.Pid != processId ||
                response.LiveLayout is null)
                throw new InvalidOperationException(response?.Message ?? error.Trim());
            return response;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("SNR読み取りツールが時間内に応答しませんでした");
        }
    }

    internal static string? FindHelper()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "RoleReaders", "SnrRoleReader.exe");
        if (File.Exists(packaged)) return packaged;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            var development = Path.Combine(directory.FullName, "tools", "SnrRoleReader", "bin",
                "Release", "net8.0", "win-x86", "SnrRoleReader.exe");
            if (File.Exists(development)) return development;
        }
        return null;
    }

    internal static IReadOnlyDictionary<int, SnrRoleData> ReadRoles(SnrLiveLayout layout,
        Func<long, int, byte[]> read)
    {
        var first = ReadSnapshot(layout, read);
        var second = ReadSnapshot(layout, read);
        if (first.Count != second.Count || first.Any(pair => !second.TryGetValue(pair.Key, out var role) ||
            pair.Value.RoleId != role.RoleId || pair.Value.ModifierId != role.ModifierId ||
            pair.Value.GhostRoleId != role.GhostRoleId))
            throw new InvalidDataException("SNR roles changed during read");
        return second;
    }

    private static Dictionary<int, SnrRoleData> ReadSnapshot(SnrLiveLayout layout,
        Func<long, int, byte[]> read)
    {
        static bool Valid(uint pointer) => pointer is >= 0x10000 and <= 0xfffffffc && pointer % 4 == 0;
        uint Pointer(long address) => BitConverter.ToUInt32(read(address, 4));
        var array = Pointer(layout.ArraySlot);
        if (!Valid(array) || Pointer(array) != layout.ArrayType ||
            Pointer(array + layout.ArrayLengthOffset) != 256)
            throw new InvalidDataException("SNR player array changed or is unavailable");
        var entries = read(array + layout.ArrayDataOffset, 1024);
        var fields = new[] { layout.Fields.PlayerId, layout.Fields.Role,
            layout.Fields.Modifier, layout.Fields.GhostRole }.Where(field => field is not null).ToArray();
        var size = fields.Max(field => field!.Offset + field.Size);
        var result = new Dictionary<int, SnrRoleData>();
        for (var id = 0; id < 256; id++)
        {
            var player = BitConverter.ToUInt32(entries, id * 4);
            if (player == 0) continue;
            if (!Valid(player)) throw new InvalidDataException("Invalid SNR player pointer");
            var bytes = read(player, size);
            if (BitConverter.ToUInt32(bytes) != layout.PlayerType ||
                ReadNumber(bytes, layout.Fields.PlayerId!) != id)
                throw new InvalidDataException("SNR player changed during read");
            var role = Describe(bytes, layout.Fields.Role!);
            (long Value, string? Name)? modifier = layout.Fields.Modifier is { } modifierField
                ? Describe(bytes, modifierField, flags: true) : null;
            (long Value, string? Name)? ghost = layout.Fields.GhostRole is { } ghostField
                ? Describe(bytes, ghostField) : null;
            var value = new SnrRoleData(checked((int)role.Value), role.Name,
                modifier is null ? null : checked((int)modifier.Value.Value), modifier?.Name,
                ghost is null ? null : checked((int)ghost.Value.Value), ghost?.Name);
            if (value.HasJumbo && layout.Jumbo is { } jumbo && ReadJumbo(player, jumbo, layout.PlayerType, read) is { } sizes)
                value = value with { JumboCurrentSize = sizes.Current, JumboMaxSize = sizes.Max };
            result.Add(id, value);
        }
        if (Pointer(layout.ArraySlot) != array ||
            !read(array + layout.ArrayDataOffset, 1024).AsSpan().SequenceEqual(entries))
            throw new InvalidDataException("SNR player array changed during read");
        return result;
    }

    private static (double Current, double Max)? ReadJumbo(uint player, SnrJumboLayout jumbo,
        uint playerType, Func<long, int, byte[]> read)
    {
        static bool Valid(uint pointer) => pointer is >= 0x10000 and <= 0xfffffffc && pointer % 4 == 0;
        uint Pointer(long address) => BitConverter.ToUInt32(read(address, 4));
        try
        {
            var list = Pointer(player + jumbo.AbilitiesOffset);
            if (!Valid(list) || Pointer(list) != jumbo.ListType) return null;
            var items = Pointer(list + jumbo.ItemsOffset);
            var count = Pointer(list + jumbo.CountOffset);
            if (!Valid(items) || Pointer(items) != jumbo.ItemsType || count > 512 ||
                count > Pointer(items + 4)) return null;
            var entries = read(items + 8, checked((int)count * 4));
            for (var i = 0; i < count; i++)
            {
                var ability = BitConverter.ToUInt32(entries, i * 4);
                if (!Valid(ability) || Pointer(ability) != jumbo.AbilityType) continue;
                var data = Pointer(ability + jumbo.DataOffset);
                if (!Valid(data) || Pointer(data) != jumbo.DataType) return null;
                var current = BitConverter.ToSingle(read(ability + jumbo.CurrentOffset, 4));
                var max = BitConverter.ToSingle(read(data + jumbo.MaxOffset, 4));
                if (!float.IsFinite(current) || current < 0 || !float.IsFinite(max) || max <= 0)
                    return null;
                if (Pointer(player) != playerType || Pointer(player + jumbo.AbilitiesOffset) != list ||
                    Pointer(list) != jumbo.ListType || Pointer(list + jumbo.ItemsOffset) != items ||
                    Pointer(list + jumbo.CountOffset) != count || Pointer(items) != jumbo.ItemsType ||
                    !read(items + 8, entries.Length).AsSpan().SequenceEqual(entries) ||
                    Pointer(ability) != jumbo.AbilityType || Pointer(ability + jumbo.DataOffset) != data ||
                    Pointer(data) != jumbo.DataType) return null;
                return (current, max);
            }
        }
        catch
        {
            // A moving Jumbo ability must not hide otherwise valid roles.
        }
        return null;
    }

    private static long ReadNumber(byte[] bytes, SnrNumberField field)
    {
        var offset = field.Offset;
        return (field.Size, field.Signed) switch
        {
            (1, false) => bytes[offset],
            (1, true) => unchecked((sbyte)bytes[offset]),
            (2, false) => BitConverter.ToUInt16(bytes, offset),
            (2, true) => BitConverter.ToInt16(bytes, offset),
            (4, false) => BitConverter.ToUInt32(bytes, offset),
            (4, true) => BitConverter.ToInt32(bytes, offset),
            _ => throw new InvalidDataException("Invalid SNR number field")
        };
    }

    private static (long Value, string? Name) Describe(byte[] bytes, SnrNumberField field, bool flags = false)
    {
        var value = ReadNumber(bytes, field);
        if (field.Names is null) return (value, null);
        if (field.Names.TryGetValue(value.ToString(CultureInfo.InvariantCulture), out var name))
            return (value, name);
        if (!flags || value <= 0) return (value, null);
        var remaining = (ulong)value;
        var names = new List<string>();
        foreach (var (key, label) in field.Names)
        {
            if (!ulong.TryParse(key, out var bit) || bit == 0 || (bit & (bit - 1)) != 0 ||
                (remaining & bit) != bit) continue;
            names.Add(label);
            remaining &= ~bit;
        }
        return (value, remaining == 0 && names.Count > 0 ? string.Join(" | ", names) : null);
    }

    private sealed class SnrDiscovery
    {
        public string Status { get; set; } = string.Empty;
        public int Pid { get; set; }
        public string? Message { get; set; }
        public SnrLiveLayout LiveLayout { get; set; } = new();
        public List<SnrHelperPlayer> Players { get; set; } = [];
    }

    private sealed class SnrHelperPlayer
    {
        public int PlayerId { get; set; }
        public SnrHelperEnum? Role { get; set; }
        public bool? IsNeutral { get; set; }
        public bool? CanKill { get; set; }
        public string? Hat2Id { get; set; }
        public string? Visor2Id { get; set; }
    }

    private sealed class SnrHelperEnum
    {
        public long Value { get; set; }
    }
}

internal sealed class SnrLiveLayout
{
    public int Pid { get; set; }
    public int PointerSize { get; set; }
    public uint ArraySlot { get; set; }
    public uint ArrayType { get; set; }
    public uint PlayerType { get; set; }
    public int ArrayLengthOffset { get; set; }
    public int ArrayDataOffset { get; set; }
    public SnrJumboLayout? Jumbo { get; set; }
    public SnrLiveFields Fields { get; set; } = new();

    public void Validate(int expectedPid)
    {
        static bool Pointer(uint value) => value is >= 0x10000 and <= 0xfffffffc && value % 4 == 0;
        if (Pid != expectedPid || PointerSize != 4 || !Pointer(ArraySlot) || !Pointer(ArrayType) ||
            !Pointer(PlayerType) || ArrayLengthOffset != 4 || ArrayDataOffset != 8 ||
            Fields.PlayerId is null || Fields.Role is null)
            throw new InvalidDataException("Invalid SNR live layout");
        foreach (var field in new[] { Fields.PlayerId, Fields.Role, Fields.Modifier, Fields.GhostRole })
        {
            if (field is null) continue;
            if (field.Offset < 4 || field.Offset > 1024 || field.Size is not (1 or 2 or 4) ||
                field.Offset + field.Size > 1028)
                throw new InvalidDataException("Invalid SNR role field");
        }
        if (Jumbo is { } jumbo && (!Pointer(jumbo.AbilityType) || !Pointer(jumbo.DataType) ||
            !Pointer(jumbo.ListType) || !Pointer(jumbo.ItemsType) ||
            new[] { jumbo.AbilitiesOffset, jumbo.ItemsOffset, jumbo.CountOffset, jumbo.CurrentOffset,
                jumbo.DataOffset, jumbo.MaxOffset }.Any(offset => offset is < 4 or > 4096 || offset % 4 != 0)))
            throw new InvalidDataException("Invalid SNR Jumbo layout");
    }
}

internal sealed class SnrLiveFields
{
    public SnrNumberField? PlayerId { get; set; }
    public SnrNumberField? Role { get; set; }
    public SnrNumberField? Modifier { get; set; }
    public SnrNumberField? GhostRole { get; set; }
}

internal sealed class SnrNumberField
{
    public int Offset { get; set; }
    public int Size { get; set; }
    public bool Signed { get; set; }
    public Dictionary<string, string>? Names { get; set; }
}

internal sealed class SnrJumboLayout
{
    public uint AbilityType { get; set; }
    public uint DataType { get; set; }
    public uint ListType { get; set; }
    public uint ItemsType { get; set; }
    public int AbilitiesOffset { get; set; }
    public int ItemsOffset { get; set; }
    public int CountOffset { get; set; }
    public int CurrentOffset { get; set; }
    public int DataOffset { get; set; }
    public int MaxOffset { get; set; }
}
