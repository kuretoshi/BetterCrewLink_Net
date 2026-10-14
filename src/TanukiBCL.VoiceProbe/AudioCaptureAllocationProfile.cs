using System.Diagnostics;

namespace TanukiBCL.VoiceProbe;

internal static class AudioCaptureAllocationProfile
{
    internal static async Task<int> RunAsync(int inputDevice, int outputDevice, TimeSpan duration,
        bool encode = false)
    {
        var frameCount = 0;
        long encodeThreadBytes = 0;
        long packetBytes = 0;
        long absoluteSamples = 0;
        var measuring = 0;
        using var codec = encode
            ? new WebRtcPeerManager("encode-profile", (_, _) => Task.CompletedTask, sendTestTone: false)
            : null;
        using var audio = new AudioDeviceSession(inputDevice, outputDevice,
            frame =>
            {
                if (codec is not null)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var encoded = codec.EncodeMonoFrame(frame.Span);
                    if (Volatile.Read(ref measuring) == 1)
                    {
                        Interlocked.Add(ref encodeThreadBytes,
                            GC.GetAllocatedBytesForCurrentThread() - before);
                        Interlocked.Add(ref packetBytes, encoded.Length);
                        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(frame.Span);
                        long absolute = 0;
                        foreach (var sample in samples) absolute += Math.Abs((int)sample);
                        Interlocked.Add(ref absoluteSamples, absolute);
                    }
                }
                Interlocked.Increment(ref frameCount);
            }, _ => { });
        audio.Start();
        await Task.Delay(TimeSpan.FromSeconds(10));
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var privateBefore = process.PrivateMemorySize64;
        var firstCount = Volatile.Read(ref frameCount);
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var watch = Stopwatch.StartNew();
        Volatile.Write(ref measuring, 1);
        await Task.Delay(duration);
        Volatile.Write(ref measuring, 0);
        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
        process.Refresh();
        var privateAfter = process.PrivateMemorySize64;
        if (codec is not null)
        {
            var encoder = typeof(WebRtcPeerManager)
                .GetField("audioEncoder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
                .GetValue(codec);
            var opus = encoder?.GetType()
                .GetField("_opusEncoder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
                .GetValue(encoder);
            Console.WriteLine($"encoderType={opus?.GetType().FullName ?? "none"} is64Bit={Environment.Is64BitProcess}");
        }
        Console.WriteLine($"{(encode ? "audioEncode" : "audioCapture")} " +
            $"frames={Volatile.Read(ref frameCount) - firstCount} " +
            (encode ? $"encodeThreadBytes={Volatile.Read(ref encodeThreadBytes)} " : "") +
            (encode ? $"packetBytes={Volatile.Read(ref packetBytes)} absoluteSamples={Volatile.Read(ref absoluteSamples)} " : "") +
            $"privateMiB={privateBefore / 1048576d:0.00}->{privateAfter / 1048576d:0.00} " +
            $"seconds={watch.Elapsed.TotalSeconds:0.0} allocatedMiB={allocated / 1048576d:0.00} " +
            $"rateMiBPerSecond={allocated / 1048576d / watch.Elapsed.TotalSeconds:0.000}");
        return 0;
    }
}
