using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TanukiBCL.VoiceProbe.GameMemory;

// The v3.2.7 NoS protocol writes RequireUpdate once, then reads published
// unmanaged snapshots without writing to the game on every poll.
internal sealed class NosSnapshotReader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private int pid = -1;
    private NosLayout? layout;
    private Task<NosLayout>? resolveTask;
    private DateTimeOffset retryAt;
    private string observedSession = string.Empty;
    private uint publication;
    private DateTimeOffset publishedAt;
    private bool fresh;

    public string Status { get; private set; } = "NoSスナップショット未取得";

    public void Reset()
    {
        pid = -1;
        layout = null;
        resolveTask = null;
        retryAt = default;
        observedSession = string.Empty;
        publication = 0;
        publishedAt = default;
        fresh = false;
        Status = "NoSスナップショット未取得";
    }

    public NosSnapshot? Update(int processId, string session, Func<long, int, byte[]> read)
    {
        if (pid != processId)
        {
            Reset();
            pid = processId;
        }

        if (resolveTask is { IsCompleted: true })
        {
            try
            {
                layout = resolveTask.GetAwaiter().GetResult();
                Status = "NoSスナップショット待機中";
            }
            catch (Exception exception)
            {
                retryAt = DateTimeOffset.UtcNow.AddSeconds(30);
                Status = $"NoS未取得: {exception.Message}";
            }
            resolveTask = null;
        }

        if (layout is null)
        {
            if (resolveTask is null && DateTimeOffset.UtcNow >= retryAt)
            {
                resolveTask = ResolveAsync(processId);
                Status = "NoSスナップショットの公開を有効化中";
            }
            return null;
        }

        try
        {
            var snapshot = ReadSnapshot(layout, read);
            if (observedSession != session)
            {
                observedSession = session;
                publication = snapshot.Publication;
                fresh = false;
            }
            if (snapshot.Publication != publication)
            {
                publication = snapshot.Publication;
                publishedAt = DateTimeOffset.UtcNow;
                fresh = true;
            }
            if (!fresh || DateTimeOffset.UtcNow - publishedAt > TimeSpan.FromSeconds(3))
            {
                Status = "NoSの新しいスナップショットを待機中";
                return null;
            }
            Status = "NoSスナップショットを自動更新中";
            return snapshot;
        }
        catch (Exception)
        {
            Status = "NoSスナップショット待機中";
            return null;
        }
    }

    private static async Task<NosLayout> ResolveAsync(int processId)
    {
        var helper = FindHelper() ?? throw new FileNotFoundException("NoS読み取りツールが見つかりません");
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
        process.StartInfo.ArgumentList.Add("layout");
        process.StartInfo.ArgumentList.Add(processId.ToString());
        if (!process.Start()) throw new InvalidOperationException("NoS読み取りツールを起動できません");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            var response = JsonSerializer.Deserialize<ResolveResult>(output, JsonOptions);
            if (process.ExitCode != 0 || response?.Status != "ok" || response.Pid != processId ||
                response.Metadata is null)
                throw new InvalidOperationException(response?.Message ?? error.Trim() ?? "NoSレイアウト取得失敗");
            response.Metadata.Validate(processId);
            return response.Metadata;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("NoS読み取りツールが時間内に応答しませんでした");
        }
    }

    internal static string? FindHelper()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "NoSReader", "TbclSnapshotReader.exe");
        if (File.Exists(packaged)) return packaged;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            var development = Path.Combine(directory.FullName, "tools", "TbclSnapshotReader", "bin",
                "Release", "net8.0", "win-x86", "TbclSnapshotReader.exe");
            if (File.Exists(development)) return development;
        }
        return null;
    }

    internal static NosSnapshot ReadSnapshot(NosLayout layout, Func<long, int, byte[]> read)
    {
        static bool ValidPointer(uint value) => value >= 0x10000 && value <= 0xfffffffc && value % 4 == 0;
        var pointer = BitConverter.ToUInt32(read((long)layout.LatestSlotAddress, 4));
        if (!ValidPointer(pointer)) throw new InvalidDataException("NoS snapshot not published");
        var s = layout.Snapshot;
        var p = layout.PlayerData;
        var headerSize = new[] { s.LocalMicPositionX, s.LocalMicPositionY, s.PlayersLength, s.Players,
            s.RadiosLength ?? 0, s.Radios ?? 0 }.Max() + 4;
        var header = read(pointer, headerSize);
        var count = BitConverter.ToInt32(header, s.PlayersLength);
        var playersAddress = BitConverter.ToUInt32(header, s.Players);
        if (count is < 0 or > 24 || count > 0 && !ValidPointer(playersAddress))
            throw new InvalidDataException("Invalid NoS players");
        var payload = count > 0 ? read(playersAddress, count * p.Size) : [];
        var radioCount = s.RadiosLength.HasValue ? BitConverter.ToInt32(header, s.RadiosLength.Value) : 0;
        var radiosAddress = s.Radios.HasValue ? BitConverter.ToUInt32(header, s.Radios.Value) : 0;
        if (radioCount is < 0 or > 32 || radioCount > 0 && !ValidPointer(radiosAddress))
            throw new InvalidDataException("Invalid NoS radios");
        var radioLayout = layout.RadioData;
        var radioPayload = radioCount > 0 && radioLayout is not null
            ? read(radiosAddress, radioCount * radioLayout.Size) : [];

        static double Finite(float value) => float.IsFinite(value)
            ? value : throw new InvalidDataException("Invalid NoS float");
        static bool Flag(byte value) => value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("Invalid NoS flag")
        };
        var players = new Dictionary<int, NosPlayerData>();
        for (var i = 0; i < count; i++)
        {
            var start = i * p.Size;
            var id = payload[start + p.PlayerId];
            var nameLength = payload[start + p.NameLength];
            if (players.ContainsKey(id) || nameLength > 32)
                throw new InvalidDataException("Invalid NoS player identity");
            players.Add(id, new NosPlayerData
            {
                PlayerId = id,
                Name = Encoding.Unicode.GetString(payload, start + p.Name, nameLength * 2),
                IsKiller = Flag(payload[start + p.IsKiller]),
                IsImpostor = Flag(payload[start + p.IsImpostor]),
                IsCrewmate = Flag(payload[start + p.IsCrewmate]),
                IsNeutral = Flag(payload[start + p.IsNeutral]),
                IsImpostorlike = Flag(payload[start + p.IsImpostorlike]),
                IsJammed = p.IsJammed.HasValue ? Flag(payload[start + p.IsJammed.Value]) : null,
                SpeakerPositionX = Finite(BitConverter.ToSingle(payload, start + p.SpeakerPositionX)),
                SpeakerPositionY = Finite(BitConverter.ToSingle(payload, start + p.SpeakerPositionY)),
                BodyRateX = p.BodyRateX.HasValue ? Finite(BitConverter.ToSingle(payload, start + p.BodyRateX.Value)) : null,
                BodyRateY = p.BodyRateY.HasValue ? Finite(BitConverter.ToSingle(payload, start + p.BodyRateY.Value)) : null,
                ColorR = Finite(BitConverter.ToSingle(payload, start + p.ColorR)),
                ColorG = Finite(BitConverter.ToSingle(payload, start + p.ColorG)),
                ColorB = Finite(BitConverter.ToSingle(payload, start + p.ColorB))
            });
        }

        var radios = new List<NosRadioData>();
        if (radioLayout is not null)
        {
            for (var i = 0; i < radioCount; i++)
            {
                var start = i * radioLayout.Size;
                var length = radioPayload[start + radioLayout.NameLength];
                if (length > 32) throw new InvalidDataException("Invalid NoS radio name");
                radios.Add(new NosRadioData(BitConverter.ToInt32(radioPayload, start + radioLayout.Kind),
                    BitConverter.ToInt32(radioPayload, start + radioLayout.HearableMask),
                    Encoding.Unicode.GetString(radioPayload, start + radioLayout.Name, length * 2)));
            }
        }

        // A ring slot can be recycled while it is being read. Discard torn snapshots.
        if (!read(pointer, headerSize).AsSpan().SequenceEqual(header) ||
            count > 0 && !read(playersAddress, payload.Length).AsSpan().SequenceEqual(payload) ||
            radioCount > 0 && radioLayout is not null &&
            !read(radiosAddress, radioPayload.Length).AsSpan().SequenceEqual(radioPayload))
            throw new InvalidDataException("NoS snapshot changed during read");
        return new NosSnapshot(pointer,
            new VoicePosition(Finite(BitConverter.ToSingle(header, s.LocalMicPositionX)),
                Finite(BitConverter.ToSingle(header, s.LocalMicPositionY))), players, radios);
    }

    private sealed class ResolveResult
    {
        public string Status { get; set; } = string.Empty;
        public int Pid { get; set; }
        public string? Message { get; set; }
        public NosLayout? Metadata { get; set; }
    }
}

internal sealed record NosSnapshot(uint Publication, VoicePosition LocalMicPosition,
    IReadOnlyDictionary<int, NosPlayerData> Players, List<NosRadioData> Radios);

internal sealed class NosLayout
{
    public int Pid { get; set; }
    public int PointerSize { get; set; }
    public int SchemaVersion { get; set; }
    public ulong LatestSlotAddress { get; set; }
    public NosSnapshotLayout Snapshot { get; set; } = new();
    public NosPlayerLayout PlayerData { get; set; } = new();
    public NosRadioLayout? RadioData { get; set; }

    public void Validate(int pid)
    {
        static bool Field(int offset, int size, int limit) => offset >= 0 && offset + size <= limit;
        if (Pid != pid || PointerSize != 4 || SchemaVersion != 20260918 ||
            LatestSlotAddress is < 0x10000 or > 0xfffffffc || LatestSlotAddress % 4 != 0 ||
            PlayerData.Size is < 96 or > 4096)
            throw new InvalidDataException("未対応のNoSスナップショット定義です");
        var s = Snapshot;
        var p = PlayerData;
        if (!Field(s.LocalMicPositionX, 4, 256) || !Field(s.LocalMicPositionY, 4, 256) ||
            !Field(s.PlayersLength, 4, 256) || !Field(s.Players, 4, 256) ||
            !Field(p.PlayerId, 1, p.Size) || !Field(p.IsKiller, 1, p.Size) ||
            !Field(p.IsImpostor, 1, p.Size) || !Field(p.IsCrewmate, 1, p.Size) ||
            !Field(p.IsNeutral, 1, p.Size) || !Field(p.IsImpostorlike, 1, p.Size) ||
            !Field(p.NameLength, 1, p.Size) || !Field(p.Name, 64, p.Size) ||
            !Field(p.SpeakerPositionX, 4, p.Size) || !Field(p.SpeakerPositionY, 4, p.Size) ||
            !Field(p.ColorR, 4, p.Size) || !Field(p.ColorG, 4, p.Size) ||
            !Field(p.ColorB, 4, p.Size) ||
            p.IsJammed.HasValue && !Field(p.IsJammed.Value, 1, p.Size) ||
            p.BodyRateX.HasValue != p.BodyRateY.HasValue ||
            p.BodyRateX.HasValue && !Field(p.BodyRateX.Value, 4, p.Size) ||
            p.BodyRateY.HasValue && !Field(p.BodyRateY.Value, 4, p.Size))
            throw new InvalidDataException("Invalid NoS field layout");
        if (s.RadiosLength.HasValue != s.Radios.HasValue || s.Radios.HasValue != (RadioData is not null))
            throw new InvalidDataException("Invalid NoS radio layout");
        if (RadioData is { } r && (r.Size is < 76 or > 4096 ||
            !Field(s.RadiosLength!.Value, 4, 256) || !Field(s.Radios!.Value, 4, 256) ||
            !Field(r.Kind, 4, r.Size) || !Field(r.HearableMask, 4, r.Size) ||
            !Field(r.NameLength, 1, r.Size) || !Field(r.Name, 64, r.Size)))
            throw new InvalidDataException("Invalid NoS radio layout");
    }
}

internal sealed class NosSnapshotLayout
{
    public int LocalMicPositionX { get; set; }
    public int LocalMicPositionY { get; set; }
    public int PlayersLength { get; set; }
    public int Players { get; set; }
    public int? RadiosLength { get; set; }
    public int? Radios { get; set; }
}

internal sealed class NosPlayerLayout
{
    public int Size { get; set; }
    public int PlayerId { get; set; }
    public int IsKiller { get; set; }
    public int IsImpostor { get; set; }
    public int IsCrewmate { get; set; }
    public int IsNeutral { get; set; }
    public int IsImpostorlike { get; set; }
    public int SpeakerPositionX { get; set; }
    public int SpeakerPositionY { get; set; }
    public int? BodyRateX { get; set; }
    public int? BodyRateY { get; set; }
    public int? IsJammed { get; set; }
    public int NameLength { get; set; }
    public int Name { get; set; }
    public int ColorR { get; set; }
    public int ColorG { get; set; }
    public int ColorB { get; set; }
}

internal sealed class NosRadioLayout
{
    public int Size { get; set; }
    public int Kind { get; set; }
    public int HearableMask { get; set; }
    public int NameLength { get; set; }
    public int Name { get; set; }
}
