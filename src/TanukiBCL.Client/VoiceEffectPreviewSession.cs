using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TanukiBCL.VoiceProbe;

namespace TanukiBCL.Client;

// Mirrors the upstream settings preview: selected mic -> disguise effect ->
// selected speaker, without sending the preview into WebRTC.
internal sealed class VoiceEffectPreviewSession : IDisposable
{
    private static readonly WaveFormat CaptureFormat = new(48_000, 16, 1);
    private readonly WaveInEvent capture;
    private readonly WaveOutEvent playback;
    private readonly BufferedWaveProvider buffer;
    private readonly NosSizeVoiceSampleProvider effect;
    private volatile bool disposed;

    private VoiceEffectPreviewSession(int inputDevice, int outputDevice, int strengthPercent)
    {
        buffer = new BufferedWaveProvider(CaptureFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        effect = new NosSizeVoiceSampleProvider(buffer.ToSampleProvider());
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
            playback.Init(new MonoToStereoSampleProvider(effect).ToWaveProvider());
        }
        catch
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.Dispose();
            playback.Dispose();
            throw;
        }
    }

    public static VoiceEffectPreviewSession Start(int inputDevice, int outputDevice, int strengthPercent)
    {
        var preview = new VoiceEffectPreviewSession(inputDevice, outputDevice, strengthPercent);
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

    public void SetStrength(int strengthPercent) => effect.SetEffect(new NosSizeVoiceEffect(
        NosSizeEffectMode.Disguise, Math.Clamp(strengthPercent, 0, 100) / 100d, 1d));

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
