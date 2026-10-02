using System.Diagnostics;
using System.IO;
using System.Text;

namespace TanukiBCL.Client;

// Release 3.2.7 keeps warning/error support logs by default and adds debug
// details only in an explicitly enabled logging mode. The inquiry form reads
// the last MiB of this file when the user chooses Send.
internal sealed class SupportLog : IDisposable
{
    private readonly StreamWriter output;
    private readonly object gate = new();
    private readonly bool debugEnabled;
    private TraceListener? traceListener;
    private TextWriter? originalOut;
    private TextWriter? originalError;

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TanukiBCL.Net", "logs", "debug.log");

    internal SupportLog(string path, bool debugEnabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        output = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false)) { AutoFlush = true };
        this.debugEnabled = debugEnabled;
    }

    internal static SupportLog Install(string[] args)
    {
        var debug = Environment.GetEnvironmentVariable("BETTERCREWLINK_LOG") == "1" ||
            Environment.GetEnvironmentVariable("BETTERCREWLINK_DEBUG_LOG") == "1" ||
            args.Contains("--log") || args.Contains("--debug-log") || args.Contains("--debugLog") ||
            Path.GetFileName(Environment.ProcessPath ?? string.Empty).Contains("debug", StringComparison.OrdinalIgnoreCase);
        var log = new SupportLog(DefaultPath, debug);
        log.originalOut = Console.Out;
        log.originalError = Console.Error;
        Console.SetOut(new CaptureWriter(log.originalOut, log, false));
        Console.SetError(new CaptureWriter(log.originalError, log, true));
        log.traceListener = new CaptureTraceListener(log);
        Trace.Listeners.Add(log.traceListener);
        log.Record(debug ? "Debug logging enabled" : "Support logging enabled", true);
        return log;
    }

    internal void Record(string? message, bool errorStream = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (!debugEnabled && !errorStream &&
            !message.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("/WARN]", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("/ERROR]", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("warning:", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("error:", StringComparison.OrdinalIgnoreCase)) return;
        lock (gate) output.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}");
    }

    private void TryRecord(string? message, bool errorStream)
    {
        try { Record(message, errorStream); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (originalOut is not null) Console.SetOut(originalOut);
        if (originalError is not null) Console.SetError(originalError);
        if (traceListener is not null) Trace.Listeners.Remove(traceListener);
        lock (gate) output.Dispose();
    }

    internal static void Verify()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tanukibcl-support-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "debug.log");
        try
        {
            using (var normal = new SupportLog(path, false))
            {
                normal.Record("[INFO] socket joined");
                normal.Record("[WARN] peer failed");
                normal.Record("[voice/WARN] relay failed");
                normal.Record("microphone failed", true);
                using var original = new StringWriter();
                var capture = new CaptureWriter(original, normal, false);
                capture.WriteLine("[voice/ERROR] capture stopped");
                if (!original.ToString().Contains("capture stopped"))
                    throw new InvalidOperationException("Console output was swallowed by support log capture");
            }
            var content = File.ReadAllText(path);
            if (content.Contains("socket joined") || !content.Contains("peer failed") ||
                !content.Contains("relay failed") || !content.Contains("capture stopped") ||
                !content.Contains("microphone failed"))
                throw new InvalidOperationException("Support log level filtering failed");
            using (var debug = new SupportLog(path, true)) debug.Record("[INFO] socket joined");
            if (!File.ReadAllText(path).Contains("socket joined"))
                throw new InvalidOperationException("Debug log did not include information events");
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
        Console.WriteLine("[PASS] Support log records warnings/errors by default and info in debug mode");
    }

    private sealed class CaptureWriter(TextWriter original, SupportLog log, bool errorStream) : TextWriter
    {
        public override Encoding Encoding => original.Encoding;
        public override void WriteLine(string? value)
        {
            original.WriteLine(value);
            log.TryRecord(value, errorStream);
        }
        public override void Write(string? value) => original.Write(value);
        public override void Flush() => original.Flush();
    }

    private sealed class CaptureTraceListener(SupportLog log) : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) => log.TryRecord(message, true);
    }
}
