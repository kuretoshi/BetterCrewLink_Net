using System.Diagnostics;
using System.Reflection;
using Concentus;
using Concentus.Enums;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace TanukiBCL.VoiceProbe;

internal static class AudioCodecAllocationProfile
{
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
        for (var frame = 0; frame < 100; frame++)
        {
            var packet = encoder.EncodeAudio(samples[frame % samples.Length], format);
            var decoded = decoder.DecodeAudio(packet, format);
            if (decoded.Length != samplesPerFrame || decoded.All(sample => sample == 0))
                throw new InvalidOperationException("Opus warmup frame did not decode to audible samples.");
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var privateBefore = process.PrivateMemorySize64;
        var processBefore = GC.GetTotalAllocatedBytes(false);
        var threadBefore = GC.GetAllocatedBytesForCurrentThread();
        var packetBytes = 0;
        var decodedSamples = 0;
        var watch = Stopwatch.StartNew();
        for (var frame = 0; frame < measuredFrames; frame++)
        {
            var packet = encoder.EncodeAudio(samples[frame % samples.Length], format);
            var decoded = decoder.DecodeAudio(packet, format);
            if (decoded.Length != samplesPerFrame)
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
            $"threadMiB={threadBytes / 1048576d:0.00} processMiB={processBytes / 1048576d:0.00} " +
            $"privateMiB={privateBefore / 1048576d:0.00}->{process.PrivateMemorySize64 / 1048576d:0.00} " +
            $"elapsedMs={watch.Elapsed.TotalMilliseconds:0}");
        return 0;
    }
}
