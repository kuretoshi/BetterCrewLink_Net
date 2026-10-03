using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TanukiBCL.VoiceProbe;

/// <summary>
/// Feeds a short, low-level tone into a selected Windows playback endpoint.
/// A virtual cable lets the normal WPF microphone capture path consume it
/// without changing the production voice sender or recording any samples.
/// </summary>
internal static class TonePlaybackTestRunner
{
    internal static async Task<int> RunAsync(ProbeOptions options, bool outputExplicit, bool vadCarrier, bool speechLike = false)
    {
        if (!outputExplicit)
            throw new ArgumentException("--play-test-tone requires an explicit --output-device.");
        if (speechLike && vadCarrier)
            throw new ArgumentException("--speech-like and --vad-tone cannot be combined.");
        if (options.Duration is not { } duration || duration <= TimeSpan.Zero ||
            duration > TimeSpan.FromSeconds(15))
            throw new ArgumentException("--play-test-tone requires --seconds between 1 and 15.");
        var devices = AudioDeviceSession.GetOutputDevices();
        if (options.OutputDevice < 0 || options.OutputDevice >= devices.Count)
            throw new ArgumentOutOfRangeException(nameof(options.OutputDevice), "Playback device does not exist.");

        var device = devices[options.OutputDevice];
        var description = speechLike ? "Syllabic 440 Hz marker + varying voiced harmonics" :
            vadCarrier ? "440 Hz test tone + 150 Hz VAD carrier" : "440 Hz test tone";
        Console.WriteLine($"{description}: output={options.OutputDevice} {device.Name}, duration={duration.TotalSeconds:0}s; no audio is saved.");
        using var output = new WaveOutEvent { DeviceNumber = options.OutputDevice };
        ISampleProvider signal;
        if (speechLike)
        {
            signal = new SpeechLikeMarkerSignal();
        }
        else
        {
            var tone = new SignalGenerator(44_100, 2)
            {
                Frequency = 440d,
                Gain = 0.08d,
                Type = SignalGeneratorType.Sin
            };
            signal = tone;
            if (vadCarrier)
            {
                var voiceBandTone = new SignalGenerator(44_100, 2)
                {
                    Frequency = 150d,
                    Gain = 0.4d,
                    Type = SignalGeneratorType.Sin
                };
                signal = new MixingSampleProvider([tone, voiceBandTone]);
            }
        }
        output.Init(new SampleToWaveProvider(signal));
        output.Play();
        try
        {
            await Task.Delay(duration);
        }
        finally
        {
            output.Stop();
        }
        return 0;
    }

    // An intentionally non-stationary, deterministic test signal: syllabic
    // amplitude changes, a moving fundamental and harmonics, plus a 440 Hz
    // marker that the existing receiver analyzer can identify. It is not speech.
    private sealed class SpeechLikeMarkerSignal : ISampleProvider
    {
        private const int SampleRate = 44_100;
        private long sampleIndex;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var time = (sampleIndex / 2) / (double)SampleRate;
                var syllable = Math.Max(0, Math.Sin(2 * Math.PI * 3.7 * time));
                var envelope = 0.04 + 0.96 * syllable * syllable;
                var movingFundamentalPhase = 2 * Math.PI *
                    (150 * time + 2.0 * Math.Sin(2 * Math.PI * 1.8 * time));
                var sample = envelope * (
                    0.08 * Math.Sin(2 * Math.PI * 440 * time) +
                    0.23 * Math.Sin(movingFundamentalPhase) +
                    0.11 * Math.Sin(2 * movingFundamentalPhase) +
                    0.06 * Math.Sin(4 * movingFundamentalPhase));
                buffer[offset + i] = (float)sample;
                sampleIndex++;
            }
            return count;
        }
    }
}
