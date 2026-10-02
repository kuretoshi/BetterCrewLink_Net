namespace TanukiBCL.VoiceProbe;

/// <summary>Metrics shown by TanukiBCL v3.2.7's ConnectionIndicator.</summary>
public sealed record ConnectionQuality(
    double? RttMs = null,
    double? JitterMs = null,
    double? LossPercent = null,
    bool Direct = false,
    double? ServerPingMs = null)
{
    public int Bars
    {
        get
        {
            var latencyMs = ServerPingMs ?? RttMs;
            if (latencyMs is null || !double.IsFinite(latencyMs.Value) || latencyMs < 0d) return 0;
            if (latencyMs >= 300d || JitterMs >= 60d) return 1;
            if (latencyMs >= 150d || JitterMs >= 30d) return 2;
            return 3;
        }
    }
}
