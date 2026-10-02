namespace TanukiBCL.VoiceProbe;

public static class MicrophoneProcessorSelfTest
{
    public static int Run()
    {
        var dry = new byte[MicrophoneProcessor.BytesPerFrame];
        for (var sample = 0; sample < MicrophoneProcessor.SamplesPerFrame; sample++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * sample / 48_000) * 4_000);
            BitConverter.TryWriteBytes(dry.AsSpan(sample * sizeof(short), sizeof(short)), value);
        }

        using (var disabled = new MicrophoneProcessor(false, false, false))
        {
            var unchanged = (byte[])dry.Clone();
            disabled.ProcessCapture(unchanged);
            if (!unchanged.AsSpan().SequenceEqual(dry)) return Fail("disabled processing changed PCM");
        }

        foreach (var flags in new[]
                 {
                     (Echo: true, Noise: false, Agc: false),
                     (Echo: false, Noise: true, Agc: false),
                     (Echo: false, Noise: false, Agc: true),
                     (Echo: true, Noise: true, Agc: true)
                 })
        {
            using var processor = new MicrophoneProcessor(flags.Echo, flags.Noise, flags.Agc);
            var playback = new float[MicrophoneProcessor.SamplesPerFrame * 2];
            var audibleFrames = 0;
            var changedFrames = 0;
            for (var frame = 0; frame < 50; frame++)
            {
                processor.SubmitPlayback(playback, 0, playback.Length);
                var capture = (byte[])dry.Clone();
                processor.ProcessCapture(capture);
                if (capture.Length != dry.Length) return Fail($"processed PCM length changed for {flags}");
                if (capture.Any(sample => sample != 0)) audibleFrames++;
                if (!capture.AsSpan().SequenceEqual(dry)) changedFrames++;
            }
            if (audibleFrames < 40) return Fail($"processed PCM missing for {flags}: {audibleFrames}/50 frames");
            if (changedFrames == 0)
                return Fail($"processing had no PCM effect for {flags}");
        }

        Console.WriteLine("[PASS] 48 kHz / 20 ms echo cancellation, noise suppression and AGC native processing");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[FAIL] {message}");
        return 1;
    }
}
