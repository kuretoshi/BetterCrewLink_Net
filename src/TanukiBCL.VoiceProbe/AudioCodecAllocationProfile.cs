using System.Diagnostics;
using System.Reflection;
using Concentus;
using Concentus.Enums;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TanukiBCL.VoiceProbe;

internal static class AudioCodecAllocationProfile
{
    internal static void VerifyFrameDurations()
    {
        var encoder = OpusCodecFactory.CreateEncoder(48_000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var decoder = OpusCodecFactory.CreateDecoder(48_000, 1);
        using var encoderLifetime = encoder as IDisposable;
        using var decoderLifetime = decoder as IDisposable;
        var packet = new byte[1_500];
        var decoded = new short[5_760];
        foreach (var frameSamples in new[] { 480, 960, 1_920, 2_880 })
        {
            var pcm = new short[frameSamples];
            for (var index = 0; index < pcm.Length; index++)
                pcm[index] = (short)(Math.Sin(2 * Math.PI * 440 * index / 48_000d) * 6_000);
            var length = encoder.Encode(pcm, frameSamples, packet, packet.Length);
            var count = decoder.Decode(packet.AsSpan(0, length), decoded, decoded.Length, false);
            if (count != frameSamples || decoded.AsSpan(0, count).IndexOfAnyExcept((short)0) < 0)
                throw new InvalidOperationException($"Opus {frameSamples}-sample frame did not decode correctly.");
        }
        Console.WriteLine("[PASS] Direct Opus decode handles 10/20/40/60 ms frames in a reusable mono buffer");
    }

    internal static int VerifyNativeInterop()
    {
        var previous = OpusCodecFactory.AttemptToUseNativeLibrary;
        IOpusEncoder? managedEncoder = null;
        IOpusEncoder? nativeEncoder = null;
        IOpusDecoder? managedDecoder = null;
        IOpusDecoder? nativeDecoder = null;
        try
        {
            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            managedEncoder = OpusCodecFactory.CreateEncoder(48_000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            managedDecoder = OpusCodecFactory.CreateDecoder(48_000, 1);
            OpusCodecFactory.AttemptToUseNativeLibrary = true;
            nativeEncoder = OpusCodecFactory.CreateEncoder(48_000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            nativeDecoder = OpusCodecFactory.CreateDecoder(48_000, 1);
            if (!nativeEncoder.GetType().FullName!.Contains("Native", StringComparison.Ordinal) ||
                !nativeDecoder.GetType().FullName!.Contains("Native", StringComparison.Ordinal))
                throw new InvalidOperationException("Native Opus is not available in this build.");
            if (nativeEncoder is not IDisposable || nativeDecoder is not IDisposable)
                throw new InvalidOperationException("Native Opus codec lifetime cannot be released.");

            managedEncoder.Bitrate = nativeEncoder.Bitrate = 51_000;
            managedEncoder.Complexity = nativeEncoder.Complexity = 9;
            var managedPacket = new byte[1_000];
            var nativePacket = new byte[1_000];
            var managedPcm = new short[960];
            var nativePcm = new short[960];
            var input = new short[960];
            var decodedNonzero = false;
            var managedPacketBytes = 0;
            var nativePacketBytes = 0;
            for (var frame = 0; frame < 32; frame++)
            {
                for (var sample = 0; sample < input.Length; sample++)
                    input[sample] = (short)(Math.Sin(2 * Math.PI * 440 *
                        (frame * input.Length + sample) / 48_000d) * 6_000);
                var managedLength = managedEncoder.Encode(input, 960, managedPacket, managedPacket.Length);
                var nativeLength = nativeEncoder.Encode(input, 960, nativePacket, nativePacket.Length);
                if (managedLength <= 0 || nativeLength <= 0)
                    throw new InvalidOperationException("One of the Opus encoders returned an empty packet.");
                var nativeSamples = nativeDecoder.Decode(managedPacket.AsSpan(0, managedLength), nativePcm, 960, false);
                var managedSamples = managedDecoder.Decode(nativePacket.AsSpan(0, nativeLength), managedPcm, 960, false);
                if (nativeSamples != 960 || managedSamples != 960)
                    throw new InvalidOperationException("Native/managed cross-decoding returned the wrong frame length.");
                if (nativePcm.AsSpan().IndexOfAnyExcept((short)0) >= 0 &&
                    managedPcm.AsSpan().IndexOfAnyExcept((short)0) >= 0) decodedNonzero = true;
                managedPacketBytes += managedLength;
                nativePacketBytes += nativeLength;
            }
            if (!decodedNonzero)
                throw new InvalidOperationException("Native/managed cross-decoding produced silence.");
            Console.WriteLine($"[PASS] Managed↔native Opus cross-decode 32 frames; packetBytes={managedPacketBytes}/{nativePacketBytes}");
            return 0;
        }
        finally
        {
            OpusCodecFactory.AttemptToUseNativeLibrary = previous;
            (managedEncoder as IDisposable)?.Dispose();
            (nativeEncoder as IDisposable)?.Dispose();
            (managedDecoder as IDisposable)?.Dispose();
            (nativeDecoder as IDisposable)?.Dispose();
        }
    }

    internal static int Run()
    {
        const int samplesPerFrame = 960;
        const int measuredFrames = 1_000;
        var format = new AudioFormat(AudioCodecsEnum.OPUS, 111, 48_000, 2, "useinbandfec=1");
        var samples = new short[32][];
        for (var frame = 0; frame < samples.Length; frame++)
        {
            var pcm = new short[samplesPerFrame];
            for (var index = 0; index < pcm.Length; index++)
            {
                var time = (frame * samplesPerFrame + index) / 48_000d;
                pcm[index] = (short)(Math.Sin(2 * Math.PI * (220 + frame * 9) * time) * 6_000);
            }
            samples[frame] = pcm;
        }

        using var encoder = new AudioEncoder(true, true);
        using var decoder = new AudioEncoder(true, true);
        var directDecoder = OpusCodecFactory.CreateDecoder(48_000, 1);
        using var directDecoderLifetime = directDecoder as IDisposable;
        var directPcm = new short[5_760];
        var maxPcmDelta = 0;
        long totalPcmDelta = 0;
        var comparedSamples = 0;
        for (var frame = 0; frame < 100; frame++)
        {
            var packet = encoder.EncodeAudio(samples[frame % samples.Length], format);
            var decoded = decoder.DecodeAudio(packet, format);
            var directCount = directDecoder.Decode(packet, directPcm, directPcm.Length, false);
            if (decoded.Length != samplesPerFrame || directCount != samplesPerFrame ||
                decoded.All(sample => sample == 0) || directPcm.AsSpan(0, directCount).IndexOfAnyExcept((short)0) < 0)
                throw new InvalidOperationException("Opus warmup frame did not decode to audible samples.");
            if (frame >= 68)
                for (var index = 0; index < decoded.Length; index++)
                {
                    var delta = Math.Abs((int)decoded[index] - directPcm[index]);
                    maxPcmDelta = Math.Max(maxPcmDelta, delta);
                    totalPcmDelta += delta;
                    comparedSamples++;
                }
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var privateBefore = process.PrivateMemorySize64;
        var processBefore = GC.GetTotalAllocatedBytes(false);
        var threadBefore = GC.GetAllocatedBytesForCurrentThread();
        var packetBytes = 0;
        var decodedSamples = 0;
        long encodeBytes = 0;
        long wrappedDecodeBytes = 0;
        long directDecodeBytes = 0;
        var watch = Stopwatch.StartNew();
        for (var frame = 0; frame < measuredFrames; frame++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var packet = encoder.EncodeAudio(samples[frame % samples.Length], format);
            encodeBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            var decoded = decoder.DecodeAudio(packet, format);
            wrappedDecodeBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            var directCount = directDecoder.Decode(packet, directPcm, directPcm.Length, false);
            directDecodeBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            if (decoded.Length != samplesPerFrame || directCount != samplesPerFrame)
                throw new InvalidOperationException("Opus frame did not decode to 960 samples.");
            packetBytes += packet.Length;
            decodedSamples += decoded.Length;
        }
        watch.Stop();
        var threadBytes = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
        var processBytes = GC.GetTotalAllocatedBytes(false) - processBefore;
        process.Refresh();
        var opus = encoder.GetType().GetField("_opusEncoder", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(encoder) as Concentus.IOpusEncoder;
        Console.WriteLine($"codecProfile encoder={opus?.GetType().FullName ?? "unknown"} " +
            $"bitrate={opus?.Bitrate} complexity={opus?.Complexity} vbr={opus?.UseVBR} fec={opus?.UseInbandFEC} " +
            $"frames={measuredFrames} packetBytes={packetBytes} decodedSamples={decodedSamples} " +
            $"encodeMiB={encodeBytes / 1048576d:0.00} wrappedDecodeMiB={wrappedDecodeBytes / 1048576d:0.00} " +
            $"directDecodeMiB={directDecodeBytes / 1048576d:0.00} " +
            $"pcmDeltaMax={maxPcmDelta} pcmDeltaMean={totalPcmDelta / (double)comparedSamples:0.00} " +
            $"threadMiB={threadBytes / 1048576d:0.00} processMiB={processBytes / 1048576d:0.00} " +
            $"privateMiB={privateBefore / 1048576d:0.00}->{process.PrivateMemorySize64 / 1048576d:0.00} " +
            $"elapsedMs={watch.Elapsed.TotalMilliseconds:0}");
        return 0;
    }
}
