using System.Text.Json;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

internal sealed record NosAddonProfile(int Pid, string Session, long Sequence, long UnixMs,
    string Phase, int LocalPlayer, bool Host, Dictionary<string, JsonElement> Channels)
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal List<T> Read<T>(string name) => Channels[name].Deserialize<List<T>>(Options) ?? [];
    internal static NosAddonProfile? Parse(JsonElement value, int pid, string token, long previous, long now)
    {
        if (value.GetProperty("kind").GetString() != "state" || value.GetProperty("protocol").GetInt32() != 1 ||
            value.GetProperty("pid").GetInt32() != pid || value.GetProperty("session").GetString() != token) return null;
        long sequence = value.GetProperty("sequence").GetInt64(), time = value.GetProperty("unixMs").GetInt64();
        string? phase = value.GetProperty("phase").GetString();
        int local = value.GetProperty("localPlayer").GetInt32();
        bool host = value.GetProperty("host").GetBoolean();
        if (sequence <= previous || sequence > 9_007_199_254_740_991 || time <= 0 || now - time > 700 || time > now + 100 ||
            phase is not ("lobby" or "tasks" or "meeting") || local is < 0 or > 255) return null;
        var channels = new Dictionary<string, JsonElement>();
        var source = value.GetProperty("channels");
        foreach (string name in new[] { "heroes", "portable", "megaphones", "dying", "interviews", "jamming", "echoes", "muted", "speech" })
        {
            var rows = source.GetProperty(name);
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > (name == "speech" ? 72 : 24)) return null;
            channels[name] = rows.Clone();
        }
        channels["ears"] = source.TryGetProperty("ears", out var ears) ? ears.Clone() : JsonSerializer.SerializeToElement(Array.Empty<int>());
        if (channels["ears"].ValueKind != JsonValueKind.Array || channels["ears"].GetArrayLength() > 24) return null;
        channels["parrots"] = source.TryGetProperty("parrots", out var parrots) ? parrots.Clone() : JsonSerializer.SerializeToElement(Array.Empty<int>());
        if (channels["parrots"].ValueKind != JsonValueKind.Array || channels["parrots"].GetArrayLength() > 120) return null;
        var profile = new NosAddonProfile(pid, token, sequence, time, phase, local, host, channels);
        return profile.Valid(now) ? profile : null;
    }
    private bool ParrotsValid(long now)
    {
        var rows = Read<NosParrotState>("parrots"); var seen = new HashSet<(int, int)>();
        return rows.All(p => p != null && p.IsValid(now) && seen.Add((p.OwnerId, p.BirdId)));
    }
    private bool Valid(long now) =>
        ParrotsValid(now) &&
        ValidRows<NosWallEarState>("ears", p => p.PlayerId, p => p.IsValid) &&
        ValidRows<NosHeroTransformState>("heroes", p => p.PlayerId, p => p.IsValid) &&
        ValidRows<NosPortableMegaphoneState>("portable", p => p.PlayerId, p => Range(p.ExtraRange, 0, 30)) &&
        ValidRows<NosMegaphoneState>("megaphones", p => p.PlayerId, p => Range(p.Energy, 0, 1) && Range(p.Range, 2, 40) &&
            (p.Phase == null || p.Phase is "charging" or "firing" && p.ShotId is > 0 and <= 9_007_199_254_740_991)) &&
        ValidRows<NosDyingVoiceState>("dying", p => p.PlayerId, p => p.Started > 0 && p.Started <= now + 100 &&
            Range(p.Seconds, 1, 30) && Range(p.Range, .5, 10) && Range(p.X, -10000, 10000) && Range(p.Y, -10000, 10000)) &&
        ValidRows<NosInterviewState>("interviews", p => p.PlayerId, p => p.IsValid(now)) &&
        ValidRows<NosVoiceJammingState>("jamming", p => p.PlayerId, p => p.IsValid) &&
        ValidRows<NosBibiriEchoState>("echoes", p => p.PlayerId, p => p.UnixMs >= 0 && p.UnixMs <= now + 100) &&
        MutedValid() && SpeechValid(now);
    private static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    private bool ValidRows<T>(string name, Func<T, int> id, Func<T, bool> valid)
    {
        var rows = Read<T>(name); var seen = new HashSet<int>();
        return rows.All(p => p is not null && id(p) is >= 0 and <= 255 && seen.Add(id(p)) && valid(p));
    }
    private bool MutedValid()
    {
        var rows = Read<int>("muted");
        return rows.All(id => id is >= 0 and <= 255) && rows.Distinct().Count() == rows.Count;
    }
    private bool SpeechValid(long now)
    {
        if (!Channels["speech"].EnumerateArray().All(HasSpeechFields)) return false;
        var rows = Read<NosHeroSpeech.PlayerRequest>("speech"); var keys = new HashSet<string>();
        return rows.All(p => p != null && p.PlayerId is >= 0 and <= 255 && p.Kind is "hero" or "ng" or "stone" &&
            keys.Add($"{p.PlayerId}|{p.Kind}") && p.Request >= 0 && p.Expires >= 0 && p.Expires <= now + 16000 &&
            p.Words is { Length: <= 3 } && p.Words.All(w => w is { Length: >= 2 and <= 32 }) &&
            (p.Kind != "ng" || p.Request > 0 && p.Words.Length > 0) &&
            (p.Kind != "stone" || p.Words.Length == 0 && p.MaxCharacters is >= 1 and <= 12));
    }
    private static bool HasSpeechFields(JsonElement row) => row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty("playerId", out _) && row.TryGetProperty("kind", out _) && row.TryGetProperty("request", out _) &&
        row.TryGetProperty("expires", out _) && row.TryGetProperty("words", out _);
}
