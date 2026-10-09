using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed record NosRoleData(int? RoleId, string? RoleName, string? DisplayName,
    string RuntimeClass, bool? IsRainbowStar, int? BodyType = null, bool? IsBerserking = null)
{
    public string Label => $"NoS: {DisplayName ?? RoleName ?? "役職名未取得"}";
}

internal sealed class NosLiveRoleReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private int pid = -1;
    private string session = string.Empty;
    private Task<NosRoleResponse>? pending;
    private DateTimeOffset nextReadAt;
    private DateTimeOffset receivedAt;
    private Dictionary<int, NosHelperRole> roles = [];

    public string Status { get; private set; } = "NoS役職未取得";

    public void Reset()
    {
        pid = -1;
        session = string.Empty;
        pending = null;
        nextReadAt = default;
        receivedAt = default;
        roles = [];
        Status = "NoS役職未取得";
    }

    public IReadOnlyDictionary<int, NosRoleData> Update(int processId, string round,
        bool useNativeBody, Func<long, int, byte[]> read)
    {
        if (pid != processId || session != round)
        {
            Reset();
            pid = processId;
            session = round;
        }

        ConsumeCompleted(processId);
        if (pending is null && DateTimeOffset.UtcNow >= nextReadAt)
        {
            pending = ReadAsync(processId);
            nextReadAt = DateTimeOffset.UtcNow.AddSeconds(2);
        }
        if (DateTimeOffset.UtcNow - receivedAt > TimeSpan.FromSeconds(5)) roles = [];

        var result = new Dictionary<int, NosRoleData>(roles.Count);
        foreach (var (id, row) in roles)
        {
            var bodyType = useNativeBody ? ReadBodyType(row.BodyLayout, id, read) : null;
            result[id] = row.Role! with
            {
                BodyType = bodyType,
                IsBerserking = row.Role!.RoleName == "berserker" && bodyType.HasValue
                    ? bodyType == 2 : null
            };
        }
        return result;
    }

    private void ConsumeCompleted(int processId)
    {
        if (pending is not { IsCompleted: true }) return;
        try
        {
            var response = pending.GetAwaiter().GetResult();
            if (response.Status != "ok" || response.Pid != processId || response.Metadata is null)
                throw new InvalidDataException(response.Message ?? "NoS役職データが不正です");
            roles = Validate(response.Metadata);
            receivedAt = DateTimeOffset.UtcNow;
            Status = roles.Count > 0 ? "NoS役職を自動更新中（約2秒間隔）" : "NoSの役職割り当てを待っています";
        }
        catch (Exception error)
        {
            roles = [];
            Status = $"NoS役職未取得: {error.Message}";
        }
        pending = null;
    }

    private static Dictionary<int, NosHelperRole> Validate(NosHelperRole[] metadata)
    {
        if (metadata.Length > 64) throw new InvalidDataException("NoS役職数が不正です");
        var result = new Dictionary<int, NosHelperRole>(metadata.Length);
        foreach (var row in metadata)
        {
            if (row.PlayerId is < 0 or > 255 || row.Role is not { } role ||
                role.RoleId is < 0 || role.RoleName?.Length > 512 || role.DisplayName?.Length > 512 ||
                string.IsNullOrWhiteSpace(role.RuntimeClass) || role.RuntimeClass.Length > 1024 ||
                !result.TryAdd(row.PlayerId, row))
                throw new InvalidDataException("NoS役職データが不正です");
        }
        return result;
    }

    private static int? ReadBodyType(NosBodyLayout? layout, int playerId, Func<long, int, byte[]> read)
    {
        if (layout is null || layout.PointerSize != 8 || !ValidPointer(layout.ControlAddress) ||
            !ValidPointer(layout.ControlClass) || !ValidPointer(layout.CosmeticsClass) ||
            !ValidOffset(layout.PlayerIdOffset) || !ValidOffset(layout.CosmeticsOffset) ||
            !ValidOffset(layout.BodyTypeOffset)) return null;
        try
        {
            ulong Pointer(ulong address) => BitConverter.ToUInt64(read(checked((long)address), 8));
            var control = layout.ControlAddress;
            if (Pointer(control) != layout.ControlClass ||
                read(checked((long)(control + (ulong)layout.PlayerIdOffset)), 1)[0] != playerId)
                return null;
            var cosmetics = Pointer(control + (ulong)layout.CosmeticsOffset);
            if (!ValidPointer(cosmetics) || Pointer(cosmetics) != layout.CosmeticsClass) return null;
            var bodyType = BitConverter.ToInt32(read(checked((long)(cosmetics + (ulong)layout.BodyTypeOffset)), 4));
            if (bodyType is < 0 or > 32 || Pointer(control) != layout.ControlClass ||
                read(checked((long)(control + (ulong)layout.PlayerIdOffset)), 1)[0] != playerId ||
                Pointer(control + (ulong)layout.CosmeticsOffset) != cosmetics) return null;
            return bodyType;
        }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException or
            InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static bool ValidPointer(ulong value) => value > 0x10000 && value <= long.MaxValue && value % 8 == 0;
    private static bool ValidOffset(int value) => value is >= 16 and <= 4096;

    private static async Task<NosRoleResponse> ReadAsync(int processId)
    {
        var helper = NosSnapshotReader.FindHelper(8) ??
            throw new FileNotFoundException("NoS役職読み取りツールが見つかりません");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(helper)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("roles");
        process.StartInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        if (!process.Start()) throw new InvalidOperationException("NoS役職読み取りツールを起動できません");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            return JsonSerializer.Deserialize<NosRoleResponse>(output, JsonOptions) ??
                throw new InvalidDataException(error.Trim());
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("NoS役職読み取りツールが時間内に応答しませんでした");
        }
    }

    private sealed class NosRoleResponse
    {
        public string Status { get; set; } = string.Empty;
        public int Pid { get; set; }
        public string? Message { get; set; }
        public NosHelperRole[]? Metadata { get; set; }
    }

    private sealed class NosHelperRole
    {
        public int PlayerId { get; set; }
        public NosRoleData? Role { get; set; }
        public NosBodyLayout? BodyLayout { get; set; }
    }

    private sealed class NosBodyLayout
    {
        public int PointerSize { get; set; }
        public ulong ControlAddress { get; set; }
        public ulong ControlClass { get; set; }
        public ulong CosmeticsClass { get; set; }
        public int PlayerIdOffset { get; set; }
        public int CosmeticsOffset { get; set; }
        public int BodyTypeOffset { get; set; }
    }
}
