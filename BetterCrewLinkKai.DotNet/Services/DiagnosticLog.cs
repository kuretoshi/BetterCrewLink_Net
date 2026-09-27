using System.IO;
using System.Text;

namespace BetterCrewLinkKai.DotNet.Services;

public static class DiagnosticLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object SyncRoot = new();

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BetterCrewLinkKai.DotNet",
        "logs");

    public static void Voice(string message)
    {
        Write("voice.log", message);
    }

    public static void WebRtc(string message)
    {
        Write("webrtc.log", message);
    }

    public static void Write(string fileName, string message)
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, fileName);
            RotateIfNeeded(path);
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxLogBytes)
        {
            return;
        }

        var previous = path + ".1";
        if (File.Exists(previous))
        {
            File.Delete(previous);
        }

        File.Move(path, previous);
    }
}
