using System.Text.Json;
using System.Text.Json.Serialization;

namespace TanukiBCL.VoiceProbe.GameMemory;

public sealed class NosMegaphoneState
{
    [JsonRequired]
    public int PlayerId { get; set; }
    [JsonRequired]
    public double Energy { get; set; }
    [JsonRequired]
    public double Range { get; set; }
    public bool? VoiceEffect { get; set; }
    public long? ShotId { get; set; }
    public string? Phase { get; set; }
}

public sealed class NosPortableMegaphoneState
{
    [JsonRequired]
    public int PlayerId { get; set; }
    [JsonRequired]
    public double ExtraRange { get; set; }
}

public sealed class NosBibiriEchoState
{
    [JsonRequired]
    public int PlayerId { get; set; }
    [JsonRequired]
    public long UnixMs { get; set; }
}

internal static class NosVoiceRoleStateReader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict
    };

    public static List<NosMegaphoneState> ReadMegaphones(int pid, long now, string? path = null) =>
        Read<NosMegaphoneState>(pid, now, path ?? PerProcessPath("megaphone-state", pid),
            item => double.IsFinite(item.Energy) && item.Energy is >= 0d and <= 1d &&
                double.IsFinite(item.Range) && item.Range is >= 2d and <= 40d &&
                (item.Phase is null ||
                 (item.Phase is "charging" or "firing" &&
                  item.ShotId is > 0 and <= 9_007_199_254_740_991L)),
            item => item.PlayerId);

    public static List<NosPortableMegaphoneState> ReadPortable(int pid, long now, string? path = null) =>
        Read<NosPortableMegaphoneState>(pid, now, path ?? DefaultPath("portable-megaphone.json"),
            item => double.IsFinite(item.ExtraRange) && item.ExtraRange is >= 0d and <= 30d,
            item => item.PlayerId);

    public static List<NosBibiriEchoState> ReadBibiri(int pid, long now, string? path = null) =>
        Read<NosBibiriEchoState>(pid, now, path ?? DefaultPath("bibiri-echo.json"),
            item => item.UnixMs >= now - 3000 && item.UnixMs <= now + 100,
            item => item.PlayerId, timestampRequired: false);

    public static List<NosInterviewState> ReadInterviews(int pid, long now, string? path = null) =>
        Read<NosInterviewState>(pid, now, path ?? DefaultPath($"interviews-{pid}.json"),
            item => item.IsValid(now), item => item.PlayerId, maxAgeMs: 1000, maxBytes: 65536, maxPlayers: 32);

    public static List<NosDyingVoiceState> ReadDyingVoices(int pid, long now, string? path = null) =>
        Read<NosDyingVoiceState>(pid, now, path ?? DefaultPath($"dying-voice-{pid}.json"),
            item => item.Started > 0 && item.Started <= now + 100 &&
                double.IsFinite(item.Seconds) && item.Seconds is >= 1d and <= 30d &&
                double.IsFinite(item.Range) && item.Range is >= .5d and <= 10d &&
                double.IsFinite(item.X) && Math.Abs(item.X) <= 10000 &&
                double.IsFinite(item.Y) && Math.Abs(item.Y) <= 10000,
            item => item.PlayerId, maxAgeMs: 1000, maxBytes: 32768);

    private static List<T> Read<T>(int pid, long now, string path, Func<T, bool> valid,
        Func<T, int> playerId, bool timestampRequired = true, int maxAgeMs = 500, int maxBytes = 16_384, int maxPlayers = 255)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > maxBytes) return [];
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(File.ReadAllText(path), Options);
            if (envelope is null || envelope.Protocol != 1 || envelope.Pid != pid ||
                envelope.Players is not { Count: <= 255 } players || players.Count > maxPlayers ||
                timestampRequired && (now - envelope.UnixMs > maxAgeMs || now - envelope.UnixMs < -100)) return [];
            var seen = new HashSet<int>();
            foreach (var item in players)
            {
                if (item is null) return [];
                var id = playerId(item);
                if (id is < 0 or > 255 || !seen.Add(id) || !valid(item)) return [];
            }
            return players;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static string DefaultPath(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TanukiBCL", "NoS", name);

    private static string PerProcessPath(string prefix, int pid)
    {
        var perProcess = DefaultPath($"{prefix}-{pid}.json");
        return File.Exists(perProcess) ? perProcess : DefaultPath($"{prefix}.json");
    }

    private sealed class Envelope<T>
    {
        [JsonRequired]
        public int Protocol { get; set; }
        [JsonRequired]
        public int Pid { get; set; }
        public long UnixMs { get; set; }
        [JsonRequired]
        public List<T>? Players { get; set; }
    }
}
