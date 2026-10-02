using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TanukiBCL.VoiceProbe;

internal static class MicrophoneCaptureRate
{
    internal const int OutputRate = 48_000;

    // The released Electron client leaves getUserMedia.sampleRate unspecified
    // normally, and requests 48000 only when oldSampleDebug is enabled.
    // WaveIn has no unspecified format: use the selected Windows endpoint's
    // shared-mode mix rate as the closest native equivalent.
    internal static int Choose(int deviceNumber, bool force48k)
    {
        if (force48k) return OutputRate;
        try
        {
            var winmmName = WaveInEvent.GetCapabilities(deviceNumber).ProductName;
            using var enumerator = new MMDeviceEnumerator();
            var matches = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Where(device => string.Equals(device.FriendlyName, winmmName, StringComparison.OrdinalIgnoreCase) ||
                    device.FriendlyName.StartsWith(winmmName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                Console.Error.WriteLine($"マイクの既定レートを特定できません（候補 {matches.Length} 件）。48 kHzを使用します。");
                return OutputRate;
            }
            var rate = matches[0].AudioClient.MixFormat.SampleRate;
            if (rate is >= 8_000 and <= 192_000) return rate;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"マイクの既定レート取得に失敗: {error.Message}。48 kHzを使用します。");
        }
        return OutputRate;
    }
}

// Converts arbitrary WinMM PCM16 callback boundaries and native sample rates
// to the 960-sample/20ms mono frames expected by DSP, VAD and Opus.
internal sealed class MicrophoneCaptureConverter
{
    private readonly int inputRate;
    private readonly Pcm16CaptureFramer framer = new();
    private readonly BufferedWaveProvider? input;
    private readonly WdlResamplingSampleProvider? resampler;
    private readonly float[] output = new float[4096];

    internal MicrophoneCaptureConverter(int inputRate)
    {
        if (inputRate is < 8_000 or > 192_000)
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        this.inputRate = inputRate;
        if (inputRate == MicrophoneCaptureRate.OutputRate) return;
        input = new BufferedWaveProvider(new WaveFormat(inputRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = false,
            ReadFully = false
        };
        resampler = new WdlResamplingSampleProvider(input.ToSampleProvider(), MicrophoneCaptureRate.OutputRate);
    }

    internal void Push(ReadOnlySpan<byte> pcm16, Action<byte[]> onFrame)
    {
        if ((pcm16.Length & 1) != 0)
            throw new ArgumentException("PCM16 input must contain complete samples", nameof(pcm16));
        if (resampler is null)
        {
            framer.Push(pcm16, onFrame);
            return;
        }
        var bytes = pcm16.ToArray();
        input!.AddSamples(bytes, 0, bytes.Length);
        // A 20ms capture callback should produce roughly 20ms of output.
        // Bound the reads in case a device/provider unexpectedly returns
        // synthesized samples after its real input is exhausted.
        var outputLimit = (int)Math.Ceiling((double)pcm16.Length / 2 *
            MicrophoneCaptureRate.OutputRate / inputRate) + output.Length;
        var produced = 0;
        while (produced < outputLimit)
        {
            var count = resampler.Read(output, 0, Math.Min(output.Length, outputLimit - produced));
            if (count <= 0) break;
            produced += count;
            var converted = new byte[count * 2];
            for (var i = 0; i < count; i++)
            {
                var sample = (short)Math.Clamp((int)Math.Round(output[i] * 32768f), short.MinValue, short.MaxValue);
                BitConverter.TryWriteBytes(converted.AsSpan(i * 2, 2), sample);
            }
            framer.Push(converted, onFrame);
        }
    }
}
