using System.Diagnostics;
using System.Text.Json;

namespace TanukiBCL.VoiceProbe.GameMemory;

/// <summary>Tracks only fresh live SNR fall-animation samples from the x64 helper.</summary>
internal sealed class SnrPushTracker : IDisposable
{
    private readonly object gate = new();
    private Process? child;
    private int pid = -1;
    private long retryAt;
    private long receivedAt;
    private int[] ids = [];

    public IReadOnlyList<int> Update(int processId)
    {
        lock (gate)
        {
            if (pid != processId) ResetCore();
            pid = processId;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (child is null && now >= retryAt) Start(processId, now);
            return now - receivedAt <= 500 ? ids : [];
        }
    }

    public void Reset()
    {
        lock (gate) ResetCore();
    }

    public void Dispose() => Reset();

    private void ResetCore()
    {
        var old = child;
        child = null;
        pid = -1;
        ids = [];
        receivedAt = 0;
        retryAt = 0;
        try { if (old is { HasExited: false }) old.Kill(); }
        catch (InvalidOperationException) { }
    }

    private void Start(int processId, long now)
    {
        retryAt = now + 10_000;
        var helper = SnrLiveRoleReader.FindHelper();
        if (helper is null) return;
        var process = new Process { StartInfo = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
        process.StartInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("--push-watch");
        process.StartInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try { if (!process.Start()) { process.Dispose(); return; } }
        catch { process.Dispose(); return; }
        child = process;
        _ = ReadLinesAsync(process, processId);
    }

    private async Task ReadLinesAsync(Process source, int processId)
    {
        try
        {
            string? line;
            while ((line = await source.StandardOutput.ReadLineAsync()) is not null)
                Accept(source, processId, line);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // A failed helper must not retain an old falling-player sample.
        }
        finally
        {
            lock (gate)
            {
                if (child == source)
                {
                    child = null;
                    ids = [];
                    receivedAt = 0;
                    retryAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000;
                }
            }
            source.Dispose();
        }
    }

    private void Accept(Process source, int processId, string line)
    {
        int[] accepted = [];
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            if (line.Length <= 65_536) accepted = Parse(line, processId, now);
        }
        catch (JsonException) { }
        lock (gate)
        {
            if (child != source) return;
            ids = accepted;
            receivedAt = accepted.Length > 0 ? now : 0;
        }
    }

    private static int[] Parse(string line, int processId, long now)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("pid", out var pidValue) || !pidValue.TryGetInt32(out var pid) || pid != processId ||
            !root.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("capturedAt", out var timestamp) ||
            !timestamp.TryGetInt64(out var captured) || captured < now - 500 || captured > now + 500 ||
            !root.TryGetProperty("playerIds", out var players) ||
            players.ValueKind != JsonValueKind.Array || players.GetArrayLength() > 256) return [];
        var result = new List<int>();
        foreach (var player in players.EnumerateArray())
        {
            if (!player.TryGetInt32(out var id) || id is < 0 or > 255) return [];
            result.Add(id);
        }
        return result.ToArray();
    }
}
