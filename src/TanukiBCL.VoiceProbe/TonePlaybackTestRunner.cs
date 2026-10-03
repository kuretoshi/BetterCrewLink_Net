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
    internal static async Task<int> RunAsync(ProbeOptions options, bool outputExplicit, bool vadCarrier)
    {
        if (!outputExplicit)
            throw new ArgumentException("--play-test-tone requires an explicit --output-device.");
        if (options.Duration is not { } duration || duration <= TimeSpan.Zero ||
            duration > TimeSpan.FromSeconds(15))
            throw new ArgumentException("--play-test-tone requires --seconds between 1 and 15.");
        var devices = AudioDeviceSession.GetOutputDevices();
        if (options.OutputDevice < 0 || options.OutputDevice >= devices.Count)
            throw new ArgumentOutOfRangeException(nameof(options.OutputDevice), "Playback device does not exist.");

        var device = devices[options.OutputDevice];
        Console.WriteLine($"440 Hz test tone{(vadCarrier ? " + 150 Hz VAD carrier" : "")}: output={options.OutputDevice} {device.Name}, duration={duration.TotalSeconds:0}s; no audio is saved.");
        using var output = new WaveOutEvent { DeviceNumber = options.OutputDevice };
        var tone = new SignalGenerator(44_100, 2)
        {
            Frequency = 440d,
            Gain = 0.08d,
            Type = SignalGeneratorType.Sin
        };
        ISampleProvider signal = tone;
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
}
