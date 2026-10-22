using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TanukiBCL.VoiceProbe;

namespace TanukiBCL.Client;

internal enum VoiceEffectPreviewKind
{
    Disguise, Jumbo, Size, Rokurokubi, Berserker, Citrus, RainbowStar, VoiceJamming, Megaphone, Bibiri
}

// Mirrors the upstream settings preview: selected mic -> disguise effect ->
// selected speaker, without sending the preview into WebRTC.
internal sealed class VoiceEffectPreviewSession : IDisposable
{
    private static readonly WaveFormat CaptureFormat = new(48_000, 16, 1);
    private readonly WaveInEvent capture;
    private readonly WaveOutEvent playback;
    private readonly BufferedWaveProvider buffer;
    private readonly NosSizeVoiceSampleProvider effect;
    private readonly PreviewEchoSampleProvider echo;
    private readonly VoiceEffectPreviewKind kind;
    private volatile bool disposed;

    private VoiceEffectPreviewSession(int inputDevice, int outputDevice, VoiceEffectPreviewKind kind, int strengthPercent)
    {
        this.kind = kind;
        buffer = new BufferedWaveProvider(CaptureFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        effect = new NosSizeVoiceSampleProvider(buffer.ToSampleProvider());
        echo = new PreviewEchoSampleProvider(effect, kind);
        SetStrength(strengthPercent);
        capture = new WaveInEvent
        {
            DeviceNumber = inputDevice,
            WaveFormat = CaptureFormat,
            BufferMilliseconds = 20,
            NumberOfBuffers = 3
        };
        playback = new WaveOutEvent
        {
            DeviceNumber = outputDevice,
            DesiredLatency = 100,
            NumberOfBuffers = 3
        };
        capture.DataAvailable += OnDataAvailable;
        try
        {
            playback.Init(new MonoToStereoSampleProvider(echo).ToWaveProvider());
        }
        catch
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.Dispose();
            playback.Dispose();
            throw;
        }
    }

    public static VoiceEffectPreviewSession Start(int inputDevice, int outputDevice,
        VoiceEffectPreviewKind kind, int strengthPercent)
    {
        var preview = new VoiceEffectPreviewSession(inputDevice, outputDevice, kind, strengthPercent);
        try
        {
            preview.playback.Play();
            preview.capture.StartRecording();
            return preview;
        }
        catch
        {
            preview.Dispose();
            throw;
        }
    }

    public void SetStrength(int strengthPercent)
    {
        var amount = Math.Clamp(strengthPercent, 0, 100) / 100d;
        echo.SetStrength((float)amount);
        effect.SetEffect(kind switch
        {
            VoiceEffectPreviewKind.Jumbo => Filter(0.4d, 0.55d, 0d, amount),
            VoiceEffectPreviewKind.Size => Filter(1.3d, 1.25d, 0d, amount),
            VoiceEffectPreviewKind.Rokurokubi => Filter(2d, 1.25d, 0d, amount),
            VoiceEffectPreviewKind.VoiceJamming => Filter(0.7d, 1d, 0d, amount),
            VoiceEffectPreviewKind.Megaphone => Filter(0.85d, 0.75d, 0.65d, amount),
            VoiceEffectPreviewKind.Berserker => new NosSizeVoiceEffect(NosSizeEffectMode.Berserker, amount, 1d),
            VoiceEffectPreviewKind.RainbowStar or VoiceEffectPreviewKind.Bibiri => null,
            _ => new NosSizeVoiceEffect(NosSizeEffectMode.Disguise, amount, 1d)
        });
    }

    private static NosSizeVoiceEffect Filter(double pitch, double formant, double squash, double amount) =>
        new(NosSizeEffectMode.SourceFilter, 0d, 1d, Squash: squash * amount,
            Pitch: 1d + (pitch - 1d) * amount, Formant: 1d + (formant - 1d) * amount);

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (!disposed && args.BytesRecorded > 0)
            buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        capture.DataAvailable -= OnDataAvailable;
        capture.StopRecording();
        playback.Stop();
        capture.Dispose();
        playback.Dispose();
    }
}

internal sealed class PreviewEchoSampleProvider(ISampleProvider source, VoiceEffectPreviewKind kind) : ISampleProvider
{
    private readonly float[] delay = new float[(int)(source.WaveFormat.SampleRate *
        (kind == VoiceEffectPreviewKind.RainbowStar ? 0.075d : 0.3d))];
    private int position;
    private float strength;
    public WaveFormat WaveFormat => source.WaveFormat;
    public void SetStrength(float value) => strength = Math.Clamp(value, 0f, 1f);

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (kind is not (VoiceEffectPreviewKind.RainbowStar or VoiceEffectPreviewKind.Bibiri)) return read;
        var feedback = kind == VoiceEffectPreviewKind.RainbowStar ? 0.08f : 0.56f;
        var wet = (kind == VoiceEffectPreviewKind.RainbowStar ? 0.08f : 0.65f) * strength;
        var dry = kind == VoiceEffectPreviewKind.RainbowStar ? 1f - 0.06f * strength : 1f;
        for (var index = offset; index < offset + read; index++)
        {
            var previous = delay[position];
            delay[position] = buffer[index] + previous * feedback;
            buffer[index] = buffer[index] * dry + previous * wet;
            if (++position == delay.Length) position = 0;
        }
        return read;
    }
}
