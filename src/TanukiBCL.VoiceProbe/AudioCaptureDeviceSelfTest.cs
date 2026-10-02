namespace TanukiBCL.VoiceProbe;

internal static class AudioCaptureDeviceSelfTest
{
    internal static async Task<int> RunAsync(ProbeOptions options)
    {
        try
        {
            foreach (var forced48k in new[] { false, true })
            {
                var frames = 0;
                using (var session = new AudioDeviceSession(options.InputDevice, options.OutputDevice,
                    _ => Interlocked.Increment(ref frames), _ => { },
                    echoCancellation: false, noiseSuppression: false, autoGainControl: false,
                    oldSampleDebug: forced48k))
                {
                    session.Start();
                    await Task.Delay(750);
                    if (frames is < 20 or > 50)
                        throw new InvalidOperationException($"{(forced48k ? "forced" : "native")} capture produced {frames} frames in 750ms");
                    Console.WriteLine($"[PASS] Device {options.InputDevice}, {(forced48k ? "forced 48k" : "native rate")}: " +
                        $"{session.CaptureSampleRate} Hz captured, {frames} complete 48 kHz frames");
                }
                await Task.Delay(100);
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[FAIL] Capture device test: {error}");
            return 1;
        }
    }
}
