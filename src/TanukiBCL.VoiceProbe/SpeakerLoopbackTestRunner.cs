using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// Observes the selected Windows render endpoint without retaining its audio.
// Detects a tone at one render endpoint. This alone cannot attribute the tone
// to WebRTC: a local monitor path can feed it even with both voice apps closed.
// An app-off control and an isolated endpoint are required for interop claims.
internal static class SpeakerLoopbackTestRunner
{
    public static int RunSelfTest()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var tone = new ToneAnalyzer(format);
        var other = new ToneAnalyzer(format);
        tone.Push(SyntheticTone(440, format));
        other.Push(SyntheticTone(880, format));
        if (!HasTone(tone.Result) || HasTone(other.Result))
            throw new InvalidOperationException("Speaker loopback tone detection failed its synthetic controls.");
        Console.WriteLine("[PASS] Speaker loopback 440 Hz detection and 880 Hz rejection.");
        return 0;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var speakerName = ReadValue(args, "--speaker-name");
        var secondsText = ReadValue(args, "--seconds");
        var seconds = secondsText is null ? 20 : int.Parse(secondsText);
        if (seconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(args), "--seconds must be between 1 and 120.");

        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToArray();
        MMDevice? device;
        if (speakerName is null)
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        else
        {
            var matches = devices.Where(candidate => candidate.FriendlyName.Contains(
                speakerName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new ArgumentException($"Speaker match count={matches.Length}; available: " +
                    string.Join(" | ", devices.Select(candidate => candidate.FriendlyName)));
            device = matches[0];
        }

        using (device)
        using (var capture = new WasapiLoopbackCapture(device))
        {
            var analyzer = new ToneAnalyzer(capture.WaveFormat);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.DataAvailable += (_, eventArgs) => analyzer.Push(eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded));
            capture.RecordingStopped += (_, eventArgs) =>
            {
                if (eventArgs.Exception is { } error) stopped.TrySetException(error);
                else stopped.TrySetResult();
            };
            Console.WriteLine($"Speaker loopback: {device.FriendlyName}, {capture.WaveFormat}, {seconds}s; no audio is saved.");
            capture.StartRecording();
            try { await Task.Delay(TimeSpan.FromSeconds(seconds)); }
            finally
            {
                capture.StopRecording();
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }

            var result = analyzer.Result;
            Console.WriteLine($"Loopback 440 Hz peak={result.ToneAmplitude:0.0000}, " +
                $"adjacent={result.AdjacentAmplitude:0.0000}, frames={result.AnalysisFrames}");
            var passed = HasTone(result);
            Console.WriteLine(passed ? "[PASS] 440 Hz reached the selected speaker endpoint." :
                "[FAIL] No distinct 440 Hz tone reached the selected speaker endpoint.");
            return passed ? 0 : 1;
        }
    }

    private static string? ReadValue(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {option}.");
        return args[index + 1];
    }

    private static bool HasTone(ToneResult result) =>
        result.AnalysisFrames > 0 && result.ToneAmplitude >= 0.003 &&
        result.ToneAmplitude >= result.AdjacentAmplitude * 3;

    private static byte[] SyntheticTone(double frequency, WaveFormat format)
    {
        var sampleCount = format.SampleRate / 2;
        var bytes = new byte[sampleCount * format.BlockAlign];
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = (float)(Math.Sin(2 * Math.PI * frequency * index / format.SampleRate) * 0.1);
            for (var channel = 0; channel < format.Channels; channel++)
                BitConverter.TryWriteBytes(bytes.AsSpan(index * format.BlockAlign + channel * sizeof(float)), sample);
        }
        return bytes;
    }

    internal sealed record ToneResult(double ToneAmplitude, double AdjacentAmplitude, int AnalysisFrames);

    internal sealed class ToneAnalyzer
    {
        private readonly WaveFormat format;
        private readonly float[] frame;
        private int position;
        private double maxTone;
        private double adjacentAtMax;
        private int analysisFrames;
        private readonly object gate = new();

        public ToneAnalyzer(WaveFormat format)
        {
            if (!((format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32) ||
                  (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)) ||
                format.Channels < 1)
                throw new NotSupportedException($"Unsupported speaker loopback format: {format}");
            this.format = format;
            frame = new float[format.SampleRate / 5];
        }

        public ToneResult Result
        {
            get { lock (gate) return new ToneResult(maxTone, adjacentAtMax, analysisFrames); }
        }

        public void Push(ReadOnlySpan<byte> bytes)
        {
            var bytesPerSample = format.BitsPerSample / 8;
            var bytesPerFrame = bytesPerSample * format.Channels;
            lock (gate)
            {
                for (var offset = 0; offset + bytesPerFrame <= bytes.Length; offset += bytesPerFrame)
                {
                    float sample = 0;
                    for (var channel = 0; channel < format.Channels; channel++)
                    {
                        var channelBytes = bytes.Slice(offset + channel * bytesPerSample, bytesPerSample);
                        sample += format.Encoding == WaveFormatEncoding.IeeeFloat
                            ? BitConverter.ToSingle(channelBytes)
                            : BitConverter.ToInt16(channelBytes) / 32768f;
                    }
                    frame[position++] = sample / format.Channels;
                    if (position != frame.Length) continue;
                    var tone = Amplitude(frame, 440, format.SampleRate);
                    var adjacent = Math.Max(Amplitude(frame, 400, format.SampleRate),
                        Amplitude(frame, 480, format.SampleRate));
                    if (tone > maxTone)
                    {
                        maxTone = tone;
                        adjacentAtMax = adjacent;
                    }
                    analysisFrames++;
                    position = 0;
                }
            }
        }

        private static double Amplitude(ReadOnlySpan<float> samples, double frequency, int sampleRate)
        {
            double real = 0, imaginary = 0;
            for (var index = 0; index < samples.Length; index++)
            {
                var phase = 2 * Math.PI * frequency * index / sampleRate;
                real += samples[index] * Math.Cos(phase);
                imaginary += samples[index] * Math.Sin(phase);
            }
            return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
        }
    }
}
