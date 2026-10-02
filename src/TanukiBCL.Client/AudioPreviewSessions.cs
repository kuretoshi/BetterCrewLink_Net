using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using TanukiBCL.VoiceProbe;

namespace TanukiBCL.Client;

internal sealed class MicrophoneLevelSession : IDisposable
{
    private readonly WaveInEvent capture;
    private readonly MicrophoneProcessor processor;
    private readonly Pcm16CaptureFramer captureFramer = new();
    private readonly Action<double> onLevel;
    private long lastReportTicks;
    private bool recording;
    private bool disposed;

    private MicrophoneLevelSession(int deviceId, bool autoGainControl, Action<double> onLevel)
    {
        this.onLevel = onLevel;
        processor = new MicrophoneProcessor(false, false, autoGainControl);
        try
        {
            capture = new WaveInEvent
            {
                DeviceNumber = deviceId,
                WaveFormat = new WaveFormat(48_000, 16, 1),
                BufferMilliseconds = 20,
                NumberOfBuffers = 3
            };
            capture.DataAvailable += OnDataAvailable;
        }
        catch
        {
            try { capture?.Dispose(); }
            finally { processor.Dispose(); }
            throw;
        }
    }

    public static MicrophoneLevelSession Start(int deviceId, bool autoGainControl, Action<double> onLevel)
    {
        var session = new MicrophoneLevelSession(deviceId, autoGainControl, onLevel);
        try
        {
            session.capture.StartRecording();
            session.recording = true;
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (disposed || args.BytesRecorded <= 0) return;
        captureFramer.Push(args.Buffer.AsSpan(0, args.BytesRecorded), ProcessCaptureFrame);
    }

    private void ProcessCaptureFrame(byte[] pcm)
    {
        if (disposed) return;
        processor.ProcessCapture(pcm);
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(lastReportTicks, now) < TimeSpan.FromMilliseconds(50)) return;
        lastReportTicks = now;
        onLevel(CalculateLevel(pcm));
    }

    internal static double CalculateLevel(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < sizeof(short)) return 0;
        double sum = 0;
        var samples = pcm.Length / sizeof(short);
        for (var index = 0; index < samples; index++)
            sum += Math.Abs(BitConverter.ToInt16(pcm.Slice(index * sizeof(short), sizeof(short))) / 32768d);
        return Math.Min(0.5d, Math.Sqrt(sum / samples)) * 200d;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        capture.DataAvailable -= OnDataAvailable;
        try
        {
            if (recording) capture.StopRecording();
        }
        finally
        {
            try { capture.Dispose(); }
            finally { processor.Dispose(); }
        }
    }
}

internal sealed class SpeakerTestSession : IDisposable
{
    private readonly AudioFileReader reader;
    private readonly WaveOutEvent playback;
    private bool disposed;

    public TimeSpan PlaybackTime => reader.CurrentTime;

    private SpeakerTestSession(int deviceId, string audioPath, Action onFinished)
    {
        reader = new AudioFileReader(audioPath);
        playback = new WaveOutEvent { DeviceNumber = deviceId, DesiredLatency = 100 };
        playback.PlaybackStopped += (_, _) => onFinished();
        try { playback.Init(reader); }
        catch
        {
            playback.Dispose();
            reader.Dispose();
            throw;
        }
    }

    public static SpeakerTestSession Start(int deviceId, string audioPath, Action onFinished)
    {
        var session = new SpeakerTestSession(deviceId, audioPath, onFinished);
        try
        {
            session.playback.Play();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        playback.Stop();
        playback.Dispose();
        reader.Dispose();
    }
}

internal static class AudioPreviewSelfTest
{
    public static void VerifyDevices()
    {
        var settings = ClientSettingsStore.Load();
        var input = AudioDeviceSession.GetInputDevices()
            .FirstOrDefault(device => device.Name == settings.MicrophoneName)
            ?? AudioDeviceSession.GetInputDevices().FirstOrDefault()
            ?? throw new InvalidOperationException("No microphone device is available");
        var output = AudioDeviceSession.GetOutputDevices()
            .FirstOrDefault(device => device.Name == settings.SpeakerName)
            ?? AudioDeviceSession.GetOutputDevices().FirstOrDefault()
            ?? throw new InvalidOperationException("No speaker device is available");
        var chimePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "chime.mp3");
        var levelEvents = 0;
        using var microphone = MicrophoneLevelSession.Start(input.Id, settings.AutoGainControl,
            _ => Interlocked.Increment(ref levelEvents));
        using var speaker = SpeakerTestSession.Start(output.Id, chimePath, () => { });
        Thread.Sleep(800);
        if (Volatile.Read(ref levelEvents) == 0 || speaker.PlaybackTime <= TimeSpan.Zero)
            throw new InvalidOperationException("Audio preview did not receive microphone or speaker frames");
    }
}
