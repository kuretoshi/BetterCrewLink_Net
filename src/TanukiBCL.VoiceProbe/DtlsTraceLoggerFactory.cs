using Microsoft.Extensions.Logging;

namespace TanukiBCL.VoiceProbe;

// Opt-in CLI diagnostics. Never format library log values: they may contain
// addresses, SDP, credentials, or other data that do not belong in test output.
internal sealed class DtlsTraceLoggerFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new DtlsTraceLogger(categoryName);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class DtlsTraceLogger(string categoryName) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // RTCPeerConnection inherits RTPSession's protected static logger.
            if (!IsEnabled(logLevel) || !categoryName.EndsWith(".RTPSession", StringComparison.Ordinal))
                return;

            var template = (state as IEnumerable<KeyValuePair<string, object?>>)?
                .FirstOrDefault(item => item.Key == "{OriginalFormat}").Value as string;
            var eventName = DescribeEvent(template);
            if (eventName is null) return;

            var role = (state as IEnumerable<KeyValuePair<string, object?>>)?
                .FirstOrDefault(item => item.Key == "IceRole").Value?.ToString();
            var safeRole = role is "active" or "passive" ? role : "unknown";
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [dtls-trace] {eventName} role={safeRole}" +
                (exception is null ? string.Empty : $" errorType={exception.GetType().Name}"));
        }

        internal static string? DescribeEvent(string? template)
        {
            if (template is null) return null;
            if (template.Contains("DTLS transport created", StringComparison.OrdinalIgnoreCase))
                return "transport-created";
            if (template.Contains("Starting DTLS handshake", StringComparison.OrdinalIgnoreCase))
                return "handshake-start";
            if (template.Contains("DTLS handshake failed", StringComparison.OrdinalIgnoreCase))
                return "handshake-failed";
            return null;
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
