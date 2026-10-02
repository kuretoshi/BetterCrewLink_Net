namespace TanukiBCL.VoiceProbe;

internal static class TanukiVoiceActivityDetectorSelfTest
{
    public static int Run()
    {
        var detector = new TanukiVoiceActivityDetector();
        var sampleOffset = 0;

        for (var block = 0; block < 10; block++)
        {
            detector.ProcessPcm16(CreateTone(150d, 0.5d, ref sampleOffset));
        }
        if (!detector.IsTalking)
        {
            Console.Error.WriteLine($"[FAIL] 150Hz voice-band tone did not start VAD; band={detector.LastBandLevel:0.000}");
            return 1;
        }

        for (var block = 0; block < 36; block++)
        {
            detector.ProcessPcm16(new byte[1024 * sizeof(short)]);
        }
        if (detector.IsTalking)
        {
            Console.Error.WriteLine("[FAIL] Silence did not stop VAD.");
            return 1;
        }

        var outsideBand = new TanukiVoiceActivityDetector();
        sampleOffset = 0;
        for (var block = 0; block < 12; block++)
        {
            outsideBand.ProcessPcm16(CreateTone(1000d, 0.5d, ref sampleOffset));
        }
        if (outsideBand.IsTalking)
        {
            Console.Error.WriteLine($"[FAIL] 1000Hz out-of-band tone started VAD; band={outsideBand.LastBandLevel:0.000}");
            return 1;
        }

        var highThreshold = new TanukiVoiceActivityDetector();
        highThreshold.SetMinimumNoiseLevel(0.8d);
        var defaultThreshold = new TanukiVoiceActivityDetector();
        sampleOffset = 0;
        for (var block = 0; block < 12; block++)
        {
            var quietTone = CreateTone(150d, 0.01d, ref sampleOffset);
            highThreshold.ProcessPcm16(quietTone);
            defaultThreshold.ProcessPcm16(quietTone);
        }
        if (highThreshold.IsTalking || !defaultThreshold.IsTalking)
        {
            Console.Error.WriteLine($"[FAIL] Sensitivity levels did not split quiet speech; default={defaultThreshold.IsTalking} high={highThreshold.IsTalking} band={highThreshold.LastBandLevel:0.000}");
            return 1;
        }

        Console.WriteLine("[PASS] 3.2.7 frequency-band VAD: speech-band start, silence stop, out-of-band rejection, sensitivity threshold");
        return 0;
    }

    private static byte[] CreateTone(double frequency, double amplitude, ref int sampleOffset)
    {
        var pcm = new byte[1024 * sizeof(short)];
        for (var sample = 0; sample < 1024; sample++)
        {
            var value = (short)Math.Round(Math.Sin(2d * Math.PI * frequency * sampleOffset++ / 48_000d) *
                                          amplitude * short.MaxValue);
            BitConverter.TryWriteBytes(pcm.AsSpan(sample * sizeof(short), sizeof(short)), value);
        }
        return pcm;
    }
}
