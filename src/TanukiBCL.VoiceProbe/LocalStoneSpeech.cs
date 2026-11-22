using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TanukiBCL.VoiceProbe;

public static class LocalStoneSpeech
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TanukiBCL", "Speech", "sherpa-2.0.2");
    public static string Error { get; private set; } = "";
    private static readonly object StatusGate = new();
    private static long nextCheck;
    private static bool available, enabled, runtimeInstalled;
    public static bool RuntimeInstalled { get { Refresh(); return runtimeInstalled; } }
    public static void RefreshStatus() => Refresh(true);
    private static void Refresh(bool force = false)
    {
        lock (StatusGate)
        {
            if (!force && Environment.TickCount64 < nextCheck) return;
            nextCheck = Environment.TickCount64 + 500;
            available = new[] { "runtime/TanukiSpeechWorker.exe", "runtime/sherpa-onnx-c-api.dll", "runtime/onnxruntime.dll", "sensevoice/model.int8.onnx", "sensevoice/tokens.txt", "silero_vad.onnx" }.All(p => File.Exists(Path.Combine(Root, p)));
            runtimeInstalled = VcRuntime.Installed(force);
            available = available && runtimeInstalled;
            try { using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,"preferences.json"))); enabled=available&&json.RootElement.GetProperty("enabled").GetBoolean(); } catch { enabled=available; }
        }
    }
    public static bool Available { get { Refresh(); return available; } }
    public static bool Enabled { get { Refresh(); return enabled; } }
    public static bool Save(bool enabled)
    {
        if (!Available) return false;
        try { File.WriteAllText(Path.Combine(Root, "preferences.json"), JsonSerializer.Serialize(new { enabled })); Error = ""; lock (StatusGate) nextCheck = 0; return true; } catch { return false; }
    }
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? process;
    private static long lastUsed;
    internal static Task<string?> TranscribeText(byte[] pcm, CancellationToken token) => Transcribe(pcm, 12, token, true);
    internal static async Task<string?> Transcribe(byte[] pcm, int maximum, CancellationToken token, bool text = false)
    {
        if (!Enabled || pcm.Length is < 3200 or > 320000) return null;
        await Gate.WaitAsync(token);
        try
        {
            if (!Enabled || token.IsCancellationRequested) return null;
            lastUsed = Environment.TickCount64;
            if (process is null || process.HasExited)
            {
                Stop();
                var info = new ProcessStartInfo(Path.Combine(Root, "runtime", "TanukiSpeechWorker.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
                info.ArgumentList.Add(Root); process = Process.Start(info) ?? throw new IOException("Speech worker unavailable");
                process.BeginErrorReadLine();
            }
            string id = Guid.NewGuid().ToString("N");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, maximum, wav = Convert.ToBase64String(VoiceStoneAudio.Wav(pcm)) }).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            string? line = await process.StandardOutput.ReadLineAsync(token);
            if (line == null || line.Length > 32768) throw new IOException("Speech worker response unavailable");
            using var result = JsonDocument.Parse(line);
            if (result.RootElement.GetProperty("id").GetString() != id || result.RootElement.TryGetProperty("error", out _)) throw new IOException("Speech worker inference failed");
            Error = "";
            if (text) { string? transcript = result.RootElement.GetProperty("text").GetString(); return transcript is { Length: <= 4096 } ? transcript : null; }
            string? word = result.RootElement.GetProperty("word").GetString();
            return VoiceStoneSpeech.ValidWord(word, maximum) ? word : null;
        }
        catch (OperationCanceledException) { Stop(); return null; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Stop(); Error = "高精度ローカル認識に失敗しました。モデル・接続を確認してください。"; return null; }
        finally { Gate.Release(); }
    }
    private static void Stop() { if (process != null) { try { if (!process.HasExited) process.Kill(); } catch { } process.Dispose(); process = null; } }
    public static void Close() { if (!Gate.Wait(0)) return; try { Stop(); } finally { Gate.Release(); } }
    internal static void Idle() { if (Environment.TickCount64 - lastUsed > 30000) Close(); }
}
