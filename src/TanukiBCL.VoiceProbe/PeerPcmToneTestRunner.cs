using Concentus;
using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System.Runtime.InteropServices;

namespace TanukiBCL.VoiceProbe;

// Measures decoded remote PCM before the local playback mixer. No audio is saved.
// A positive result distinguishes inbound media from an output-endpoint leak.
internal static class PeerPcmToneTestRunner
{
    public static async Task<int> RunAsync(ProbeOptions baseOptions)
    {
        if (baseOptions.GameProcessId is null || baseOptions.ExpectedPeerClientId is null)
            throw new ArgumentException("--peer-pcm-tone-test requires --game-process-id and --expected-peer-client-id.");

        var duration = baseOptions.Duration ?? TimeSpan.FromSeconds(30);
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(baseOptions), "Duration must be between 1 and 120 seconds.");
        VerifyIndependentDecoder();

        var options = baseOptions with
        {
            Duration = null,
            LiveAudio = true,
            SelfTest = false,
            TanukiInteropTest = false
        };
        var analyzer = new SpeakerLoopbackTestRunner.ToneAnalyzer(new WaveFormat(48_000, 16, 2));
        var frames = 0;
        var dataReady = 0;
        var sampleCount = 0L;
        var sumSquares = 0d;
        var peak = 0d;
        var sampleGate = new object();
        var encodedCount = 0L;
        var encodedBytes = 0L;
        var encodedMin = int.MaxValue;
        var encodedMax = 0;
        var encodedGate = new object();
        var independentMono = new IndependentOpusAnalyzer(1);
        var independentStereo = new IndependentOpusAnalyzer(2);
        using var cancellation = new CancellationTokenSource(duration);
        await using var probe = new VoiceServerProbe(options, "pcm-tone", receiveOnly: true);
        probe.PeerConnectionStatusChanged += (clientId, state) =>
        {
            if (clientId == options.ExpectedPeerClientId && state.Equals("data-ready", StringComparison.OrdinalIgnoreCase))
                Interlocked.Exchange(ref dataReady, 1);
        };
        probe.PeerPcmReceived += (clientId, pcm) =>
        {
            if (clientId != options.ExpectedPeerClientId) return;
            Interlocked.Increment(ref frames);
            analyzer.Push(MemoryMarshal.AsBytes(pcm.AsSpan()));
            double frameSquares = 0, framePeak = 0;
            foreach (var sample in pcm)
            {
                var normalized = sample / 32768d;
                frameSquares += normalized * normalized;
                framePeak = Math.Max(framePeak, Math.Abs(normalized));
            }
            lock (sampleGate)
            {
                sampleCount += pcm.Length;
                sumSquares += frameSquares;
                peak = Math.Max(peak, framePeak);
            }
        };
        probe.PeerEncodedAudioReceived += (clientId, length) =>
        {
            if (clientId != options.ExpectedPeerClientId) return;
            lock (encodedGate)
            {
                encodedCount++;
                encodedBytes += length;
                encodedMin = Math.Min(encodedMin, length);
                encodedMax = Math.Max(encodedMax, length);
            }
        };
        probe.PeerEncodedAudioPacketReceived += (clientId, packet) =>
        {
            if (clientId != options.ExpectedPeerClientId) return;
            lock (encodedGate)
            {
                independentMono.Push(packet);
                independentStereo.Push(packet);
            }
        };

        Console.WriteLine($"Decoded-PCM tone test: game PID={options.GameProcessId}, " +
            $"peer client={options.ExpectedPeerClientId}, duration={duration.TotalSeconds:0}s; no audio is saved.");
        try { await probe.RunAsync(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

        var result = analyzer.Result;
        double rms, pcmPeak;
        lock (sampleGate)
        {
            rms = sampleCount == 0 ? 0 : Math.Sqrt(sumSquares / sampleCount);
            pcmPeak = peak;
        }
        long packetCount;
        double meanPacketBytes;
        int minPacketBytes, maxPacketBytes;
        double monoRms, monoPeak, stereoRms, stereoPeak;
        int monoErrors, stereoErrors;
        lock (encodedGate)
        {
            packetCount = encodedCount;
            meanPacketBytes = encodedCount == 0 ? 0 : (double)encodedBytes / encodedCount;
            minPacketBytes = encodedCount == 0 ? 0 : encodedMin;
            maxPacketBytes = encodedMax;
            monoRms = independentMono.Rms;
            monoPeak = independentMono.Peak;
            stereoRms = independentStereo.Rms;
            stereoPeak = independentStereo.Peak;
            monoErrors = independentMono.DecodeErrors;
            stereoErrors = independentStereo.DecodeErrors;
        }
        Console.WriteLine($"Peer data-ready={Volatile.Read(ref dataReady) != 0}, PCM callbacks={frames}, " +
            $"PCM RMS={rms:0.0000}, peak={pcmPeak:0.0000}, " +
            $"Opus packets={packetCount}, bytes min/avg/max={minPacketBytes}/{meanPacketBytes:0.0}/{maxPacketBytes}, " +
            $"440 Hz peak={result.ToneAmplitude:0.0000}, adjacent={result.AdjacentAmplitude:0.0000}, " +
            $"analysis frames={result.AnalysisFrames}");
        Console.WriteLine($"Independent Opus decode (48 kHz, {independentMono.Backend}): " +
            $"mono RMS={monoRms:0.0000}, peak={monoPeak:0.0000}, errors={monoErrors}; " +
            $"stereo RMS={stereoRms:0.0000}, peak={stereoPeak:0.0000}, errors={stereoErrors}. " +
            "Packet bytes and decoded audio are not saved.");
        var passed = Volatile.Read(ref dataReady) != 0 && frames > 0 && result.AnalysisFrames > 0 &&
                     result.ToneAmplitude >= 0.003 && result.ToneAmplitude >= result.AdjacentAmplitude * 3;
        Console.WriteLine(passed ? "[PASS] The target peer supplied decoded 440 Hz PCM." :
            "[FAIL] No distinct decoded 440 Hz tone from the target peer.");
        return passed ? 0 : 1;
    }

    internal static void VerifyIndependentDecoder()
    {
        var tone = new short[960];
        for (var index = 0; index < tone.Length; index++)
            tone[index] = (short)(Math.Sin(2d * Math.PI * 440d * index / 48_000d) * 8_000d);
        var format = new AudioFormat(AudioCodecsEnum.OPUS, 111, 48_000, 2, "useinbandfec=1");
        var encoded = new AudioEncoder(true, true).EncodeAudio(tone, format);
        var mono = new IndependentOpusAnalyzer(1);
        var stereo = new IndependentOpusAnalyzer(2);
        mono.Push(encoded);
        stereo.Push(encoded);
        if (mono.DecodeErrors != 0 || stereo.DecodeErrors != 0 ||
            mono.Rms < 0.01d || stereo.Rms < 0.01d)
            throw new InvalidOperationException("Independent mono/stereo Opus diagnostic failed its positive tone control.");
    }

    private sealed class IndependentOpusAnalyzer
    {
        private const int MaxSamplesPerChannel = 5_760;
        private readonly int channels;
        private readonly IOpusDecoder decoder;
        private readonly short[] pcm;
        private long sampleCount;
        private double sumSquares;

        public IndependentOpusAnalyzer(int channels)
        {
            this.channels = channels;
            decoder = OpusCodecFactory.CreateDecoder(48_000, channels);
            pcm = new short[MaxSamplesPerChannel * channels];
        }

        public double Rms => sampleCount == 0 ? 0 : Math.Sqrt(sumSquares / sampleCount);
        public string Backend => decoder.GetType().Name;
        public double Peak { get; private set; }
        public int DecodeErrors { get; private set; }

        public void Push(ReadOnlyMemory<byte> packet)
        {
            try
            {
                var samplesPerChannel = decoder.Decode(packet.Span, pcm, MaxSamplesPerChannel, false);
                for (var index = 0; index < samplesPerChannel * channels; index++)
                {
                    var normalized = pcm[index] / 32768d;
                    sumSquares += normalized * normalized;
                    Peak = Math.Max(Peak, Math.Abs(normalized));
                }
                sampleCount += samplesPerChannel * channels;
            }
            catch
            {
                DecodeErrors++;
            }
        }
    }
}
