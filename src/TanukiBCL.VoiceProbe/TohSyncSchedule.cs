namespace TanukiBCL.VoiceProbe;

internal static class TohSyncSchedule
{
    internal static readonly TimeSpan Refresh = TimeSpan.FromSeconds(3);

    internal static bool ShouldSend(string signature, string previous, DateTimeOffset sentAt,
        DateTimeOffset now) => signature != previous || now - sentAt >= Refresh;

    internal static void Verify()
    {
        var now = DateTimeOffset.UtcNow;
        if (!ShouldSend("catalog", "", now, now) ||
            ShouldSend("catalog", "catalog", now, now.AddMilliseconds(2999)) ||
            !ShouldSend("catalog", "catalog", now, now.AddSeconds(3)) ||
            !ShouldSend("updated", "catalog", now, now.AddMilliseconds(1)))
            throw new InvalidOperationException("TOH role or catalog sync did not honor changes and the 3-second refresh.");
    }
}
