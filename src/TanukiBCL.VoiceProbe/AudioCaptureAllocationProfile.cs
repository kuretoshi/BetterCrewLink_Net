using System.Diagnostics;

namespace TanukiBCL.VoiceProbe;

internal static class AudioCaptureAllocationProfile
{
    internal static async Task<int> RunAsync(int inputDevice, int outputDevice, TimeSpan duration)
    {
        var frameCount = 0;
        using var audio = new AudioDeviceSession(inputDevice, outputDevice,
            _ => Interlocked.Increment(ref frameCount), _ => { });
        audio.Start();
        await Task.Delay(TimeSpan.FromSeconds(10));
        var firstCount = Volatile.Read(ref frameCount);
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var watch = Stopwatch.StartNew();
        await Task.Delay(duration);
        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
        Console.WriteLine($"audioCapture frames={Volatile.Read(ref frameCount) - firstCount} " +
            $"seconds={watch.Elapsed.TotalSeconds:0.0} allocatedMiB={allocated / 1048576d:0.00} " +
            $"rateMiBPerSecond={allocated / 1048576d / watch.Elapsed.TotalSeconds:0.000}");
        return 0;
    }
}
