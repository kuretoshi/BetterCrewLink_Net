using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TanukiBCL.VoiceProbe.GameMemory;

// NoS v3.5.3 publishes snapshots continuously. The 3.2.9 reader is read-only
// and uses the x64 helper for the supported 64-bit game architecture.
internal sealed class NosSnapshotReader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private int pid = -1;
    private int pointerSize;
    private NosLayout? layout;
    private Task<NosLayout>? resolveTask;
    private DateTimeOffset retryAt;
    private DateTimeOffset? failureSince;
    private TimeSpan retryDelay = TimeSpan.FromSeconds(5);
    private string observedSession = string.Empty;
    private ulong publication;
    private DateTimeOffset publishedAt;
    private bool fresh;
    private string? lastFailure;

    public string Status { get; private set; } = "NoSスナップショット未取得";

    public int? SchemaVersion => layout?.SchemaVersion;

    public void Reset()
    {
        pid = -1;
        pointerSize = 0;
        layout = null;
        resolveTask = null;
        retryAt = default;
        failureSince = null;
        retryDelay = TimeSpan.FromSeconds(5);
        observedSession = string.Empty;
        publication = 0;
        publishedAt = default;
        fresh = false;
        lastFailure = null;
        Status = "NoSスナップショット未取得";
    }

    // 3.2.9: five seconds of failed or stalled reads discard the layout and resolve it again.
    private void FailedRead(string reason)
    {
        lastFailure = reason;
        failureSince ??= DateTimeOffset.UtcNow;
        Status = $"{reason}（自動再取得中）";
        if (DateTimeOffset.UtcNow - failureSince < TimeSpan.FromSeconds(5)) return;
        layout = null;
        failureSince = null;
        observedSession = string.Empty;
        publication = 0;
        publishedAt = default;
        fresh = false;
        retryAt = DateTimeOffset.UtcNow;
        Status = $"{reason}（読み取り位置を再取得します）";
    }

    internal static string ReadFailureReason(string raw) => raw switch
    {
        "NoS snapshot not published" => $"NoSからデータが公開されていません（{raw}）",
        "Invalid NoS players" => $"プレイヤー一覧の件数またはメモリ位置が不正です（{raw}）",
        "Invalid NoS radios" => $"無線情報の件数またはメモリ位置が不正です（{raw}）",
        "Invalid NoS float" => $"位置・色・サイズ・首の長さの数値が不正です（{raw}）",
        "Invalid NoS body state" => $"BodyTypeまたはNeckLengthが不正です（{raw}）",
        "Invalid NoS flag" => $"役職などの判定値が不正です（{raw}）",
        "Invalid NoS player identity" => $"プレイヤーIDが重複しているか、名前の長さが不正です（{raw}）",
        "Invalid NoS costume name" => $"コスチューム名の長さが不正です（{raw}）",
        "Invalid NoS radio name" => $"無線名の長さが不正です（{raw}）",
        "NoS snapshot changed during read" => $"読み取り中にNoSのデータが更新されました（{raw}）",
        _ => raw
    };

    public NosSnapshot? Update(int processId, string session, int targetPointerSize,
        Func<long, int, byte[]> read)
    {
        if (targetPointerSize != 8)
        {
            Reset();
            Status = "NoS読取は64ビット版Among Usのみ対応";
            return null;
        }
        if (pid != processId || pointerSize != targetPointerSize)
        {
            Reset();
            pid = processId;
            pointerSize = targetPointerSize;
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
                // 3.2.9 retries helper crashes as well as MOD errors, backing off from 5 to 30 seconds.
                lastFailure = $"NoS未取得: {exception.Message}";
                Status = $"{lastFailure}（{retryDelay.TotalSeconds:0}秒後に自動再取得）";
                retryAt = DateTimeOffset.UtcNow + retryDelay;
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
            resolveTask = null;
        }

        if (layout is null)
        {
            if (resolveTask is null && DateTimeOffset.UtcNow >= retryAt)
            {
                resolveTask = ResolveAsync(processId, targetPointerSize);
                Status = lastFailure is null
                    ? "NoSスナップショットの公開を有効化中…"
                    : $"{lastFailure}（読み取り位置を自動再取得中）";
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
                FailedRead(fresh ? "NoSデータの更新が3秒以上停止しています" : "NoSの新しいデータがまだ公開されていません");
                return null;
            }
            failureSince = null;
            retryDelay = TimeSpan.FromSeconds(5);
            lastFailure = null;
            Status = "NoSスナップショットを自動更新中";
            return snapshot;
        }
        catch (Exception exception)
        {
            FailedRead($"NoS読み取り失敗: {ReadFailureReason(exception.Message)}");
            return null;
        }
    }

    private static async Task<NosLayout> ResolveAsync(int processId, int targetPointerSize)
    {
        var helper = FindHelper(targetPointerSize) ??
            throw new FileNotFoundException("NoS読み取りツールが見つかりません");
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
            if (response.Metadata.PointerSize != targetPointerSize)
                throw new InvalidDataException("NoS補助リーダーのアーキテクチャが一致しません");
            return response.Metadata;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("NoS読み取りツールが時間内に応答しませんでした");
        }
    }

    internal static string? FindHelper(int pointerSize)
    {
        if (pointerSize != 8) return null;
        var packaged = Path.Combine(AppContext.BaseDirectory, "NoSReader", "TbclSnapshotReader.exe");
        if (File.Exists(packaged)) return packaged;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            var development = Path.Combine(directory.FullName, "tools", "TbclSnapshotReader", "bin",
                "Release", "net8.0", "win-x64", "TbclSnapshotReader.exe");
            if (File.Exists(development)) return development;
        }
        return null;
    }

    internal static NosSnapshot ReadSnapshot(NosLayout layout, Func<long, int, byte[]> read)
    {
        bool ValidPointer(ulong value) => value >= 0x10000 && value <= long.MaxValue &&
            value % (ulong)layout.PointerSize == 0;
        ulong ReadPointer(long address) => layout.PointerSize == 4
            ? BitConverter.ToUInt32(read(address, 4))
            : BitConverter.ToUInt64(read(address, 8));
        var pointer = ReadPointer(checked((long)layout.LatestSlotAddress));
        if (!ValidPointer(pointer)) throw new InvalidDataException("NoS snapshot not published");
        var s = layout.Snapshot;
        var p = layout.PlayerData;
        var headerSize = new[] { s.LocalMicPositionX + 4, s.LocalMicPositionY + 4,
            s.PlayersLength + 4, s.Players + layout.PointerSize,
            (s.RadiosLength ?? 0) + 4, (s.Radios ?? 0) + layout.PointerSize }.Max();
        var header = read(checked((long)pointer), headerSize);
        ulong HeaderPointer(int offset) => layout.PointerSize == 4
            ? BitConverter.ToUInt32(header, offset) : BitConverter.ToUInt64(header, offset);
        var count = BitConverter.ToInt32(header, s.PlayersLength);
        var playersAddress = HeaderPointer(s.Players);
        if (count is < 0 or > 24 || count > 0 && !ValidPointer(playersAddress))
            throw new InvalidDataException("Invalid NoS players");
        var payload = count > 0 ? read(checked((long)playersAddress), count * p.Size) : [];
        var radioCount = s.RadiosLength.HasValue ? BitConverter.ToInt32(header, s.RadiosLength.Value) : 0;
        var radiosAddress = s.Radios.HasValue ? HeaderPointer(s.Radios.Value) : 0;
        if (radioCount is < 0 or > 32 || radioCount > 0 && !ValidPointer(radiosAddress))
            throw new InvalidDataException("Invalid NoS radios");
        var radioLayout = layout.RadioData;
        var radioPayload = radioCount > 0 && radioLayout is not null
            ? read(checked((long)radiosAddress), radioCount * radioLayout.Size) : [];

        static double Finite(float value) => float.IsFinite(value)
            ? value : throw new InvalidDataException("Invalid NoS float");
        static bool Flag(byte value) => value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("Invalid NoS flag")
        };
        static NosCostumeData? Costume(byte[] payload, int start, NosCostumeLayout? costume)
        {
            if (costume is null) return null;
            var offset = start + costume.Offset;
            var length = payload[offset + costume.NameLength];
            if (length > costume.Capacity) throw new InvalidDataException("Invalid NoS costume name");
            return new NosCostumeData(Encoding.Unicode.GetString(payload, offset + costume.Name, length * 2));
        }
        var players = new Dictionary<int, NosPlayerData>();
        for (var i = 0; i < count; i++)
        {
            var start = i * p.Size;
            var id = payload[start + p.PlayerId];
            var nameLength = payload[start + p.NameLength];
            var bodyType = p.BodyType.HasValue ? BitConverter.ToInt32(payload, start + p.BodyType.Value) : (int?)null;
            var neckLength = p.NeckLength.HasValue
                ? Finite(BitConverter.ToSingle(payload, start + p.NeckLength.Value)) : (double?)null;
            if (bodyType is < 0 or > 32 || neckLength < 0d)
                throw new InvalidDataException("Invalid NoS body state");
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
                BodyType = bodyType,
                NeckLength = neckLength,
                ColorR = Finite(BitConverter.ToSingle(payload, start + p.ColorR)),
                ColorG = Finite(BitConverter.ToSingle(payload, start + p.ColorG)),
                ColorB = Finite(BitConverter.ToSingle(payload, start + p.ColorB)),
                Skin = Costume(payload, start, p.Skin),
                Hat = Costume(payload, start, p.Hat),
                Visor = Costume(payload, start, p.Visor)
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
        if (!read(checked((long)pointer), headerSize).AsSpan().SequenceEqual(header) ||
            count > 0 && !read(checked((long)playersAddress), payload.Length).AsSpan().SequenceEqual(payload) ||
            radioCount > 0 && radioLayout is not null &&
            !read(checked((long)radiosAddress), radioPayload.Length).AsSpan().SequenceEqual(radioPayload))
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

internal sealed record NosSnapshot(ulong Publication, VoicePosition LocalMicPosition,
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
        static bool Field(int offset, int size, int limit) => size > 0 && offset >= 0 && offset + size <= limit;
        if (Pid != pid || PointerSize != 8 || SchemaVersion is not (20260918 or 20260928 or 20261005 or 20261009) ||
            LatestSlotAddress is < 0x10000 or > long.MaxValue ||
            LatestSlotAddress % (ulong)PointerSize != 0 ||
            PlayerData.Size is < 96 or > 4096)
            throw new InvalidDataException("未対応のNoSスナップショット定義です");
        var s = Snapshot;
        var p = PlayerData;
        if (!Field(s.LocalMicPositionX, 4, 256) || !Field(s.LocalMicPositionY, 4, 256) ||
            !Field(s.PlayersLength, 4, 256) || !Field(s.Players, PointerSize, 256) ||
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
            p.BodyRateY.HasValue && !Field(p.BodyRateY.Value, 4, p.Size) ||
            p.BodyType.HasValue != p.NeckLength.HasValue ||
            p.BodyType.HasValue && !Field(p.BodyType.Value, 4, p.Size) ||
            p.NeckLength.HasValue && !Field(p.NeckLength.Value, 4, p.Size) ||
            SchemaVersion == 20261009 && (!p.BodyType.HasValue || !p.BodyRateX.HasValue))
            throw new InvalidDataException("Invalid NoS field layout");
        NosCostumeLayout?[] costumes = [p.Skin, p.Hat, p.Visor];
        var costumeCount = costumes.Count(costume => costume is not null);
        if (costumeCount is not (0 or 3) || SchemaVersion >= 20261005 && costumeCount != 3 ||
            costumes.Any(c => c is not null && (c.Capacity is <= 0 or > 1024 ||
                !Field(c.Offset, c.Size, p.Size) || !Field(c.NameLength, 1, c.Size) ||
                !Field(c.Name, c.Capacity * 2, c.Size))))
            throw new InvalidDataException("Invalid NoS costume layout");
        if (s.RadiosLength.HasValue != s.Radios.HasValue || s.Radios.HasValue != (RadioData is not null))
            throw new InvalidDataException("Invalid NoS radio layout");
        if (RadioData is { } r && (r.Size is < 76 or > 4096 ||
            !Field(s.RadiosLength!.Value, 4, 256) || !Field(s.Radios!.Value, PointerSize, 256) ||
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
    public int? BodyType { get; set; }
    public int? NeckLength { get; set; }
    public int? IsJammed { get; set; }
    public int NameLength { get; set; }
    public int Name { get; set; }
    public int ColorR { get; set; }
    public int ColorG { get; set; }
    public int ColorB { get; set; }
    public NosCostumeLayout? Skin { get; set; }
    public NosCostumeLayout? Hat { get; set; }
    public NosCostumeLayout? Visor { get; set; }
}

internal sealed class NosCostumeLayout
{
    public int Offset { get; set; }
    public int NameLength { get; set; }
    public int Name { get; set; }
    public int Capacity { get; set; }
    public int Size { get; set; }
}

internal sealed class NosRadioLayout
{
    public int Size { get; set; }
    public int Kind { get; set; }
    public int HearableMask { get; set; }
    public int NameLength { get; set; }
    public int Name { get; set; }
}
