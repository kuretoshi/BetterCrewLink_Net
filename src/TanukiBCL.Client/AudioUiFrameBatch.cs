namespace TanukiBCL.Client;

// Audio callbacks can run 50 times a second per peer. Keep their numerical
// diagnostics bounded and update WPF on a timer instead of posting each frame.
internal sealed class AudioUiFrameBatch
{
    private const int MaxQueuedLevels = 500;
    private readonly object gate = new();
    private Queue<ReceivedLevel> pendingLevels = new(64);
    private Queue<ReceivedLevel> spareLevels = new(64);
    private Dictionary<int, int> pendingReceived = [];
    private Dictionary<int, int> spareReceived = [];
    private long pendingSent;
    private int latestSentPeerCount;

    internal void AddReceived(int clientId, PcmLevelFrame level, DateTimeOffset at)
    {
        lock (gate)
        {
            pendingReceived.TryGetValue(clientId, out var count);
            pendingReceived[clientId] = checked(count + 1);
            if (pendingLevels.Count == MaxQueuedLevels) pendingLevels.Dequeue();
            pendingLevels.Enqueue(new ReceivedLevel(clientId, level, at));
        }
    }

    internal void AddSent(int peerCount)
    {
        lock (gate)
        {
            pendingSent++;
            latestSentPeerCount = peerCount;
        }
    }

    internal void Drain(Action<int, int> received, Action<int, PcmLevelFrame, DateTimeOffset> level,
        Action<long, int> sent)
    {
        Queue<ReceivedLevel> levels;
        Dictionary<int, int> counts;
        long sentCount;
        int sentPeerCount;
        lock (gate)
        {
            levels = pendingLevels;
            pendingLevels = spareLevels;
            spareLevels = levels;
            counts = pendingReceived;
            pendingReceived = spareReceived;
            spareReceived = counts;
            sentCount = pendingSent;
            sentPeerCount = latestSentPeerCount;
            pendingSent = 0;
        }

        try
        {
            foreach (var (clientId, count) in counts) received(clientId, count);
            foreach (var frame in levels) level(frame.ClientId, frame.Level, frame.At);
            if (sentCount > 0) sent(sentCount, sentPeerCount);
        }
        finally
        {
            counts.Clear();
            levels.Clear();
        }
    }

    internal static void Verify()
    {
        var batch = new AudioUiFrameBatch();
        var now = DateTimeOffset.UnixEpoch;
        for (var frame = 0; frame < 600; frame++)
        {
            batch.AddReceived(7, new PcmLevelFrame(frame, frame), now.AddMilliseconds(frame));
            batch.AddSent(2);
        }
        batch.AddReceived(8, new PcmLevelFrame(8, 8), now);
        var received = new Dictionary<int, int>();
        var retained = 0;
        var firstRetained = -1d;
        long sentCount = 0;
        batch.Drain((clientId, count) => received[clientId] = count,
            (_, level, _) =>
            {
                if (retained++ == 0) firstRetained = level.Rms;
            },
            (count, peers) =>
            {
                sentCount = count;
                if (peers != 2) throw new InvalidOperationException("Audio UI peer count changed");
            });
        if (received[7] != 600 || received[8] != 1 || retained != MaxQueuedLevels ||
            firstRetained != 101d || sentCount != 600)
            throw new InvalidOperationException("Audio UI frame batch lost counts or exceeded its level cap");
        batch.Drain((_, _) => throw new InvalidOperationException("Audio UI count was retained"),
            (_, _, _) => throw new InvalidOperationException("Audio UI level was retained"),
            (_, _) => throw new InvalidOperationException("Audio UI send count was retained"));
        batch.AddReceived(7, default, now);
        batch.Drain((_, _) => { }, (_, _, _) => { }, (_, _) => { });
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 10_000; frame++)
        {
            batch.AddReceived(7, default, now);
            batch.AddSent(2);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated != 0)
            throw new InvalidOperationException($"Audio UI callbacks allocated {allocated} bytes");
        Console.WriteLine("[PASS] Audio UI frame updates are bounded and keep exact frame totals");
    }

    private readonly record struct ReceivedLevel(int ClientId, PcmLevelFrame Level, DateTimeOffset At);
}
