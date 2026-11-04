using System.Text.Json;

namespace TanukiBCL.VoiceProbe;

internal static class NosLimitSpeechState
{
    private static readonly Dictionary<int, List<int>> remembered = [];
    internal static List<int> Read(int pid, bool active, NosAddonProfile? linked)
    {
        if (!active) { remembered.Remove(pid); return []; }
        if (linked != null) { remembered[pid] = linked.Read<int>("muted"); return remembered[pid]; }
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TanukiBCL", "NoS", $"limit-speech-{pid}.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return Previous(pid);
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); var d = doc.RootElement;
            long time = d.GetProperty("unixMs").GetInt64(), now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (d.GetProperty("protocol").GetInt32() != 1 || d.GetProperty("pid").GetInt32() != pid || now - time > 1000 || time > now + 100) return Previous(pid);
            var ids = d.GetProperty("players").Deserialize<List<int>>();
            if (ids == null || ids.Count > 24 || ids.Any(id => id is < 0 or > 255) || ids.Distinct().Count() != ids.Count) return Previous(pid);
            remembered[pid] = ids; return ids;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return Previous(pid); }
    }
    private static List<int> Previous(int pid) => remembered.GetValueOrDefault(pid) ?? [];
}
