using TanukiBCL.VoiceProbe;

namespace TanukiBCL.Client;

// The compact view consumes this map synchronously on the WPF dispatcher.
// Keep stable peer rows rather than allocating a dictionary and a status
// record on every audio/UI refresh.
internal sealed class VoicePlayerStatusCache
{
    private readonly Dictionary<int, VoicePlayerStatus> statuses = [];
    private readonly HashSet<int> seenIds = [];
    private readonly List<int> staleIds = [];

    internal void Begin() => seenIds.Clear();

    internal void Set(int clientId, string connectionState, bool talking, bool usingRadio,
        ConnectionQuality? quality)
    {
        seenIds.Add(clientId);
        if (statuses.TryGetValue(clientId, out var previous) &&
            previous.ConnectionState == connectionState && previous.Talking == talking &&
            previous.UsingRadio == usingRadio && Equals(previous.Quality, quality)) return;
        statuses[clientId] = new VoicePlayerStatus(connectionState, talking, usingRadio, quality);
    }

    internal IReadOnlyDictionary<int, VoicePlayerStatus> End()
    {
        staleIds.Clear();
        foreach (var clientId in statuses.Keys)
            if (!seenIds.Contains(clientId)) staleIds.Add(clientId);
        foreach (var clientId in staleIds) statuses.Remove(clientId);
        return statuses;
    }

    internal static void Verify()
    {
        var cache = new VoicePlayerStatusCache();
        var quality = new ConnectionQuality(ServerPingMs: 25d);
        cache.Begin();
        cache.Set(7, "connected", true, false, quality);
        var first = cache.End()[7];
        for (var index = 0; index < 1000; index++)
        {
            cache.Begin();
            cache.Set(7, "connected", true, false, quality);
            cache.End();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            cache.Begin();
            cache.Set(7, "connected", true, false, quality);
            cache.End();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated > 4096 || !ReferenceEquals(first, cache.End()[7]))
            throw new InvalidOperationException($"Stable peer status allocated {allocated} bytes.");
        var rows = new[] { (ClientId: 7, ConnectionState: "connected", Talking: true,
            UsingRadio: false, Quality: quality) };
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
            GC.KeepAlive(rows.ToDictionary(row => row.ClientId, row => new VoicePlayerStatus(
                row.ConnectionState, row.Talking, row.UsingRadio, row.Quality)));
        var previousAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        if (previousAllocation <= allocated)
            throw new InvalidOperationException("Stable status cache did not reduce update allocations.");

        cache.Begin();
        cache.Set(7, "disconnected", false, false, null);
        var changed = cache.End()[7];
        if (ReferenceEquals(first, changed) || changed.ConnectionState != "disconnected")
            throw new InvalidOperationException("Changed peer status was not published.");
        cache.Begin();
        if (cache.End().Count != 0)
            throw new InvalidOperationException("Departed peer remained in the compact status map.");
        Console.WriteLine($"[PASS] Stable compact peer statuses: {allocated} vs " +
            $"{previousAllocation} bytes / 10,000 updates");
    }
}
