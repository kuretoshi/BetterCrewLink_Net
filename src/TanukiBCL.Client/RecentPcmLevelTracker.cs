namespace TanukiBCL.Client;

internal readonly record struct PcmLevelFrame(double Rms, double Peak);

internal readonly record struct RecentPcmLevelSnapshot(int Frames, double Rms, double Peak);

// Keeps only numerical levels, never the received audio samples.
internal sealed class RecentPcmLevelTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private const int MaxFrames = 500;
    private readonly Queue<(DateTimeOffset At, PcmLevelFrame Level)> frames = new();

    internal static PcmLevelFrame Measure(ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty) return default;
        double sumSquares = 0;
        var peak = 0;
        foreach (var sample in pcm)
        {
            var magnitude = Math.Abs((int)sample);
            sumSquares += (double)sample * sample;
            peak = Math.Max(peak, magnitude);
        }
        const double scale = 32768d;
        return new PcmLevelFrame(Math.Sqrt(sumSquares / pcm.Length) / scale, peak / scale);
    }

    internal void Add(PcmLevelFrame level, DateTimeOffset at)
    {
        frames.Enqueue((at, level));
        Trim(at);
        while (frames.Count > MaxFrames) frames.Dequeue();
    }

    internal RecentPcmLevelSnapshot Snapshot(DateTimeOffset at)
    {
        Trim(at);
        if (frames.Count == 0) return default;
        double sumSquares = 0;
        double peak = 0;
        foreach (var (_, level) in frames)
        {
            sumSquares += level.Rms * level.Rms;
            peak = Math.Max(peak, level.Peak);
        }
        return new RecentPcmLevelSnapshot(frames.Count,
            Math.Sqrt(sumSquares / frames.Count), peak);
    }

    internal void Clear() => frames.Clear();

    private void Trim(DateTimeOffset at)
    {
        while (frames.TryPeek(out var oldest) && at - oldest.At > Window)
            frames.Dequeue();
    }

    internal static void Verify()
    {
        var silence = Measure(new short[960]);
        var signal = Measure(Enumerable.Repeat((short)16384, 960).ToArray());
        var minimum = Measure([short.MinValue]);
        if (silence != default || Math.Abs(signal.Rms - 0.5d) > 0.00001d ||
            Math.Abs(signal.Peak - 0.5d) > 0.00001d ||
            minimum.Peak != 1d || Measure([]) != default)
            throw new InvalidOperationException("Decoded PCM level measurement is incorrect");

        var tracker = new RecentPcmLevelTracker();
        var start = DateTimeOffset.UnixEpoch;
        tracker.Add(silence, start);
        tracker.Add(signal, start.AddSeconds(1));
        var active = tracker.Snapshot(start.AddSeconds(1));
        if (active.Frames != 2 || Math.Abs(active.Rms - Math.Sqrt(0.125d)) > 0.00001d ||
            Math.Abs(active.Peak - 0.5d) > 0.00001d ||
            tracker.Snapshot(start.AddSeconds(7)).Frames != 0)
            throw new InvalidOperationException("Recent decoded PCM window is incorrect");
        tracker.Add(signal, start.AddSeconds(8));
        tracker.Clear();
        if (tracker.Snapshot(start.AddSeconds(8)).Frames != 0)
            throw new InvalidOperationException("Recent decoded PCM was not cleared");
        for (var i = 0; i < 600; i++) tracker.Add(signal, start.AddSeconds(9));
        if (tracker.Snapshot(start.AddSeconds(9)).Frames != MaxFrames)
            throw new InvalidOperationException("Recent decoded PCM frame count is not bounded");
        Console.WriteLine("[PASS] Five-second decoded PCM level diagnostic retains only numeric RMS/peak");
    }
}
