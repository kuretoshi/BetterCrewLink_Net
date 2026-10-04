using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

public static class MicrophoneProcessorSelfTest
{
    public static int Run()
    {
        try
        {
            VerifyDisabled();
            VerifyCaptureFraming();
            VerifyCaptureRateConversion();
            VerifyEchoCancellation();
            VerifyNoiseSuppression();
            VerifyAutomaticGain();
            Console.WriteLine("[PASS] Synthetic input-processing outcomes (not hardware/acoustic or Chromium parity)");
            return 0;
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine($"[FAIL] {error.Message}");
            return 1;
        }
    }

    private static void VerifyDisabled()
    {
        using var processor = new MicrophoneProcessor(false, false, false);
        var dry = CreateFrame(sample => Math.Sin(2 * Math.PI * 440 * sample / 48_000) * 4_000);
        var unchanged = (byte[])dry.Clone();
        processor.ProcessCapture(unchanged);
        Require(unchanged.AsSpan().SequenceEqual(dry), "disabled processing changed PCM");
    }

    private static void VerifyEchoCancellation()
    {
        const int frames = 750;
        const int measureFrom = 500;
        var random = new Random(327);
        var farEnd = new short[frames * MicrophoneProcessor.SamplesPerFrame];
        double filtered = 0;
        for (var i = 0; i < farEnd.Length; i++)
        {
            filtered = filtered * 0.7 + (random.NextDouble() * 2 - 1) * 5_000;
            farEnd[i] = (short)filtered;
        }

        using var processor = new MicrophoneProcessor(true, false, false);
        var playback = new float[MicrophoneProcessor.SamplesPerFrame * 2];
        double inputEnergy = 0, outputEnergy = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var start = frame * MicrophoneProcessor.SamplesPerFrame;
            for (var sample = 0; sample < MicrophoneProcessor.SamplesPerFrame; sample++)
                playback[sample * 2] = playback[sample * 2 + 1] = farEnd[start + sample] / 32768f;
            processor.SubmitPlayback(playback, 0, playback.Length);
            // Fixed 60 ms transport delay plus a quieter reflected path, both
            // within the 200 ms filter. No near-end speech in this ERLE window.
            var capture = CreateFrame(sample =>
                FarEndAt(start + sample - 2_880) * 0.5 + FarEndAt(start + sample - 3_280) * 0.2);
            if (frame >= measureFrom) inputEnergy += Energy(capture);
            processor.ProcessCapture(capture);
            if (frame >= measureFrom) outputEnergy += Energy(capture);
        }
        var reductionDb = 10 * Math.Log10(inputEnergy / Math.Max(1, outputEnergy));
        Console.WriteLine($"AEC synthetic delayed two-path echo reduction: {reductionDb:F1} dB");
        Require(reductionDb >= 12, $"AEC synthetic echo reduction was only {reductionDb:F1} dB");

        // Cancelling everything would also pass the echo-only check. Verify
        // that independent near-end speech survives when the render is silent.
        Array.Clear(playback);
        inputEnergy = outputEnergy = 0;
        for (var frame = 0; frame < 100; frame++)
        {
            processor.SubmitPlayback(playback, 0, playback.Length);
            var capture = CreateFrame(sample => Math.Sin(2 * Math.PI * 440 *
                (sample + frame * MicrophoneProcessor.SamplesPerFrame) / 48_000) * 4_000);
            if (frame >= 50) inputEnergy += Energy(capture);
            processor.ProcessCapture(capture);
            if (frame >= 50) outputEnergy += Energy(capture);
        }
        var nearEndRatio = Math.Sqrt(outputEnergy / inputEnergy);
        Require(nearEndRatio is > 0.8 and < 1.2, $"AEC damaged independent near-end input: RMS ratio {nearEndRatio:F3}");
        return;

        short FarEndAt(int index) => index < 0 ? (short)0 : farEnd[index];
    }

    private static void VerifyCaptureFraming()
    {
        var source = new byte[MicrophoneProcessor.BytesPerFrame * 4];
        new Random(20).NextBytes(source);
        var frames = new List<byte[]>();
        var framer = new Pcm16CaptureFramer();
        // Split inside a PCM16 sample, inside a DSP frame, and across multiple
        // frames. No partial frame may reach the native processor or network.
        var offset = 0;
        foreach (var length in new[] { 1, 317, 1_601, 1, 2_113, source.Length - 4_033 })
        {
            framer.Push(source.AsSpan(offset, length), frames.Add);
            offset += length;
            Require(frames.Count == offset / MicrophoneProcessor.BytesPerFrame,
                "capture framer emitted incomplete or missing frames");
        }
        Require(frames.All(frame => frame.Length == MicrophoneProcessor.BytesPerFrame),
            "capture framer changed the 20 ms frame size");
        Require(frames.SelectMany(frame => frame).SequenceEqual(source),
            "capture framer lost, repeated or reordered PCM bytes");

        // The live device callback consumes frames synchronously; this mode may
        // reuse the same backing array but must preserve each completed frame.
        var transientFramer = new Pcm16CaptureFramer();
        byte[]? firstBuffer = null;
        var transientFrames = 0;
        transientFramer.Push(source, frame =>
        {
            firstBuffer ??= frame;
            Require(ReferenceEquals(firstBuffer, frame), "transient capture allocated a new frame");
            Require(frame.AsSpan().SequenceEqual(source.AsSpan(transientFrames * frame.Length, frame.Length)),
                "transient capture changed PCM frame contents");
            transientFrames++;
        }, reuseFrame: true);
        Require(transientFrames == 4, "transient capture missed a completed frame");
        var singleFrame = source.AsSpan(0, MicrophoneProcessor.BytesPerFrame);
        for (var i = 0; i < 1_000; i++) transientFramer.Push(singleFrame, static _ => { }, reuseFrame: true);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) transientFramer.Push(singleFrame, static _ => { }, reuseFrame: true);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated <= 8_192, $"transient capture allocated {allocated} bytes per 10,000 frames");
        Console.WriteLine($"[PASS] Transient 20 ms capture reuses one frame ({allocated} bytes / 10,000 warmed frames)");
    }

    private static void VerifyCaptureRateConversion()
    {
        var waveBuffer = new BufferedWaveProvider(new WaveFormat(48_000, 16, 1)) { ReadFully = false };
        byte[] addSamplesInput = [0x34, 0x12, 0x78, 0x56];
        waveBuffer.AddSamples(addSamplesInput, 0, addSamplesInput.Length);
        Array.Clear(addSamplesInput);
        var copiedSamples = new byte[4];
        Require(waveBuffer.Read(copiedSamples, 0, copiedSamples.Length) == copiedSamples.Length &&
            copiedSamples.SequenceEqual(new byte[] { 0x34, 0x12, 0x78, 0x56 }),
            "NAudio BufferedWaveProvider did not copy the supplied input bytes");
        var oversizedConverter = new MicrophoneCaptureConverter(44_100);
        oversizedConverter.Push(new byte[16 * 1024 + 2], static _ => { }, reuseFrame: true);
        Require(oversizedConverter.RetainedInputCopyBufferBytes == 0,
            "an oversized microphone callback was retained for the session lifetime");

        foreach (var inputRate in new[] { 44_100, 48_000, 96_000 })
        {
            const int durationMs = 1_000;
            var source = new byte[inputRate * durationMs / 1_000 * 2];
            for (var i = 0; i < source.Length / 2; i++)
            {
                var sample = (short)Math.Round(10_000 * Math.Sin(2 * Math.PI * 1_000 * i / inputRate));
                BitConverter.TryWriteBytes(source.AsSpan(i * 2, 2), sample);
            }
            var converter = new MicrophoneCaptureConverter(inputRate);
            var frames = new List<byte[]>();
            var offset = 0;
            // Vary callback sizes, including multiple 20ms frames in one callback.
            while (offset < source.Length)
            {
                var chunk = Math.Min(source.Length - offset,
                    new[] { 176, 2_048, 3_522, 7_680 }[(offset / 2) % 4]);
                chunk &= ~1;
                converter.Push(source.AsSpan(offset, chunk), frames.Add);
                offset += chunk;
            }
            Require(frames.Count is >= 47 and <= 50,
                $"{inputRate} Hz capture produced {frames.Count} Opus frames, expected about 50");
            Require(frames.All(frame => frame.Length == MicrophoneProcessor.BytesPerFrame),
                $"{inputRate} Hz capture emitted a partial DSP frame");
            var decoded = frames.SelectMany(frame => frame).ToArray();
            var transientConverter = new MicrophoneCaptureConverter(inputRate);
            var transientBytes = new List<byte>(decoded.Length);
            offset = 0;
            while (offset < source.Length)
            {
                var chunk = Math.Min(source.Length - offset,
                    new[] { 176, 2_048, 3_522, 7_680 }[(offset / 2) % 4]);
                chunk &= ~1;
                transientConverter.Push(source.AsSpan(offset, chunk), frame => transientBytes.AddRange(frame),
                    reuseFrame: true);
                offset += chunk;
            }
            Require(transientBytes.SequenceEqual(decoded),
                $"{inputRate} Hz transient capture changed the converted PCM bytes");
            var zeroCrossings = 0;
            double energy = 0;
            var previous = (short)0;
            for (var i = 4_800; i < decoded.Length / 2; i++)
            {
                var sample = BitConverter.ToInt16(decoded, i * 2);
                if (previous < 0 && sample >= 0) zeroCrossings++;
                energy += (double)sample * sample;
                previous = sample;
            }
            var seconds = (decoded.Length / 2 - 4_800) / 48_000d;
            var frequency = zeroCrossings / seconds;
            var rms = Math.Sqrt(energy / (decoded.Length / 2 - 4_800));
            Require(Math.Abs(frequency - 1_000) < 15 && rms is > 6_000 and < 8_000,
                $"{inputRate} Hz capture changed tone to {frequency:F1} Hz / RMS {rms:F0}");
        }
        Console.WriteLine("[PASS] Native 44.1/48/96 kHz microphone callbacks become continuous 48 kHz 20ms PCM with preserved pitch");
    }

    private static void VerifyNoiseSuppression()
    {
        using var processor = new MicrophoneProcessor(false, true, false);
        var random = new Random(1234);
        double inputEnergy = 0, outputEnergy = 0;
        for (var frame = 0; frame < 400; frame++)
        {
            var capture = CreateFrame(_ => (random.NextDouble() * 2 - 1) * 1_000);
            if (frame >= 250) inputEnergy += Energy(capture);
            processor.ProcessCapture(capture);
            if (frame >= 250) outputEnergy += Energy(capture);
        }
        var reductionDb = 10 * Math.Log10(inputEnergy / Math.Max(1, outputEnergy));
        Console.WriteLine($"NS synthetic stationary noise reduction: {reductionDb:F1} dB");
        Require(reductionDb >= 6, $"noise suppression reduced stationary noise by only {reductionDb:F1} dB");
    }

    private static void VerifyAutomaticGain()
    {
        using var processor = new MicrophoneProcessor(false, false, true);
        double inputEnergy = 0, outputEnergy = 0;
        var clippedSamples = 0;
        for (var frame = 0; frame < 1_000; frame++)
        {
            var capture = CreateFrame(sample =>
            {
                var time = (sample + frame * MicrophoneProcessor.SamplesPerFrame) / 48_000d;
                return (Math.Sin(2 * Math.PI * 220 * time) + Math.Sin(2 * Math.PI * 440 * time) * 0.3) * 400;
            });
            if (frame >= 750) inputEnergy += Energy(capture);
            processor.ProcessCapture(capture);
            if (frame < 750) continue;
            outputEnergy += Energy(capture);
            for (var index = 0; index < capture.Length; index += sizeof(short))
                if (Math.Abs((int)BitConverter.ToInt16(capture, index)) >= 32_767) clippedSamples++;
        }
        var gain = Math.Sqrt(outputEnergy / inputEnergy);
        Console.WriteLine($"AGC synthetic low-level input gain: {gain:F1}x, clipped samples: {clippedSamples}");
        Require(gain > 2, $"automatic gain did not raise low-level input sufficiently: {gain:F1}x");
        Require(clippedSamples == 0, "automatic gain clipped synthetic low-level input");
    }

    private static byte[] CreateFrame(Func<int, double> sampleAt)
    {
        var frame = new byte[MicrophoneProcessor.BytesPerFrame];
        for (var sample = 0; sample < MicrophoneProcessor.SamplesPerFrame; sample++)
            BitConverter.TryWriteBytes(frame.AsSpan(sample * sizeof(short), sizeof(short)), (short)sampleAt(sample));
        return frame;
    }

    private static double Energy(byte[] frame)
    {
        double energy = 0;
        for (var index = 0; index < frame.Length; index += sizeof(short))
        {
            var sample = (double)BitConverter.ToInt16(frame, index);
            energy += sample * sample;
        }
        return energy;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
