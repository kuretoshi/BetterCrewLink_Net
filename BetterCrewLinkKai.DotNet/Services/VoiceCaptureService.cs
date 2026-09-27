using BetterCrewLinkKai.DotNet.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class VoiceCaptureService : IDisposable
{
    private const int NetworkSampleRate = 48000;
    private readonly MMDeviceEnumerator enumerator = new();
    private IWaveIn? capture;
    private bool isTransmitting;
    private double gain = 1;
    private bool noiseSuppressionEnabled;
    private bool echoCancellationEnabled;
    private double highPassLastInput;
    private double highPassLastOutput;
    private double noiseFloor = 0.006;
    private WaveFormat? networkWaveFormat;
    private bool disposed;

    public bool IsRunning => capture is not null;

    public WaveFormat? WaveFormat => networkWaveFormat ?? capture?.WaveFormat;

    public event EventHandler<VoiceAudioFrame>? FrameCaptured;

    public event EventHandler<double>? LevelChanged;

    public void Start(
        string deviceId,
        double microphoneGain,
        bool gainEnabled,
        bool noiseSuppression,
        bool echoCancellation)
    {
        Stop();
        gain = gainEnabled ? Math.Clamp(microphoneGain / 100d, 0d, 2d) : 1d;
        noiseSuppressionEnabled = noiseSuppression;
        echoCancellationEnabled = echoCancellation;
        highPassLastInput = 0;
        highPassLastOutput = 0;
        noiseFloor = 0.006;
        networkWaveFormat = null;
        capture = CreateMicrophoneCapture(deviceId);
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += (_, _) => LevelChanged?.Invoke(this, 0);
        capture.StartRecording();
    }

    public void Stop()
    {
        if (capture is null)
        {
            return;
        }

        capture.DataAvailable -= OnDataAvailable;
        capture.StopRecording();
        capture.Dispose();
        capture = null;
        networkWaveFormat = null;
        LevelChanged?.Invoke(this, 0);
    }

    public void SetTransmitting(bool transmitting)
    {
        isTransmitting = transmitting;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Stop();
        enumerator.Dispose();
    }

    private IWaveIn CreateMicrophoneCapture(string deviceId)
    {
        try
        {
            var device = GetCaptureDevice(deviceId);
            var wasapi = device is null
                ? new WasapiCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                : new WasapiCapture(device);
            wasapi.ShareMode = AudioClientShareMode.Shared;
            return wasapi;
        }
        catch
        {
            return new WaveInEvent
            {
                DeviceNumber = 0,
                BufferMilliseconds = 20,
                WaveFormat = new WaveFormat(48000, 16, 1)
            };
        }
    }

    private MMDevice? GetCaptureDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            string.Equals(deviceId, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(device => string.Equals(device.ID, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (capture is null || e.BytesRecorded <= 0)
        {
            LevelChanged?.Invoke(this, 0);
            return;
        }

        var buffer = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);
        ApplyGain(buffer, capture.WaveFormat);
        ApplyVoiceProcessing(buffer, capture.WaveFormat);

        var networkBuffer = NormalizeForNetwork(buffer, capture.WaveFormat, out var networkFormat);
        networkWaveFormat = networkFormat;
        var level = CalculateLevel(networkBuffer, networkFormat);
        LevelChanged?.Invoke(this, level);
        FrameCaptured?.Invoke(this, new VoiceAudioFrame(networkBuffer, networkFormat, level, isTransmitting));
    }

    private void ApplyGain(byte[] buffer, WaveFormat format)
    {
        if (Math.Abs(gain - 1d) < 0.001)
        {
            return;
        }

        var bytesPerSample = format.BitsPerSample / 8;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            for (var i = 0; i + 3 < buffer.Length; i += 4)
            {
                var sample = Math.Clamp(BitConverter.ToSingle(buffer, i) * gain, -1d, 1d);
                BitConverter.GetBytes((float)sample).CopyTo(buffer, i);
            }
        }
        else if (bytesPerSample == 2)
        {
            for (var i = 0; i + 1 < buffer.Length; i += 2)
            {
                var sample = Math.Clamp(BitConverter.ToInt16(buffer, i) * gain, short.MinValue, short.MaxValue);
                BitConverter.GetBytes((short)sample).CopyTo(buffer, i);
            }
        }
    }

    private void ApplyVoiceProcessing(byte[] buffer, WaveFormat format)
    {
        if (!noiseSuppressionEnabled && !echoCancellationEnabled)
        {
            return;
        }

        var bytesPerSample = format.BitsPerSample / 8;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            for (var i = 0; i + 3 < buffer.Length; i += 4)
            {
                var sample = ProcessSample(BitConverter.ToSingle(buffer, i));
                BitConverter.GetBytes((float)sample).CopyTo(buffer, i);
            }
        }
        else if (bytesPerSample == 2)
        {
            for (var i = 0; i + 1 < buffer.Length; i += 2)
            {
                var sample = BitConverter.ToInt16(buffer, i) / 32768d;
                var processed = (short)Math.Clamp(ProcessSample(sample) * 32767d, short.MinValue, short.MaxValue);
                BitConverter.GetBytes(processed).CopyTo(buffer, i);
            }
        }
    }

    private double ProcessSample(double sample)
    {
        if (echoCancellationEnabled)
        {
            const double highPass = 0.985;
            var filtered = highPass * (highPassLastOutput + sample - highPassLastInput);
            highPassLastInput = sample;
            highPassLastOutput = filtered;
            sample = filtered;
        }

        if (!noiseSuppressionEnabled)
        {
            return Math.Clamp(sample, -1d, 1d);
        }

        var level = Math.Abs(sample);
        if (level < 0.04)
        {
            noiseFloor = (noiseFloor * 0.995) + (level * 0.005);
        }

        var threshold = Math.Clamp(noiseFloor * 3.2, 0.008, 0.045);
        if (level <= threshold)
        {
            sample *= 0.08;
        }
        else if (level <= threshold * 2.2)
        {
            var mix = (level - threshold) / (threshold * 1.2);
            sample *= Math.Clamp(mix, 0.08, 1d);
        }

        return Math.Clamp(sample, -1d, 1d);
    }

    private static double CalculateLevel(byte[] buffer, WaveFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            return 0;
        }

        var sampleCount = Math.Max(1, buffer.Length / bytesPerSample);
        double total = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            for (var i = 0; i + 3 < buffer.Length; i += 4)
            {
                var sample = BitConverter.ToSingle(buffer, i);
                total += sample * sample;
            }
        }
        else if (bytesPerSample == 2)
        {
            for (var i = 0; i + 1 < buffer.Length; i += 2)
            {
                var sample = BitConverter.ToInt16(buffer, i) / 32768d;
                total += sample * sample;
            }
        }

        return Math.Clamp(Math.Sqrt(total / sampleCount) * 220, 0, 100);
    }

    private static byte[] NormalizeForNetwork(byte[] buffer, WaveFormat format, out WaveFormat networkFormat)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = format.BitsPerSample / 8;
        networkFormat = new WaveFormat(NetworkSampleRate, 16, 1);
        byte[] monoPcm16;

        if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2 && channels == 1)
        {
            monoPcm16 = buffer;
        }
        else if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            monoPcm16 = ConvertFloatToPcm16Mono(buffer, channels);
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2)
        {
            monoPcm16 = ConvertPcm16ToMono(buffer, channels);
        }
        else
        {
            networkFormat = format;
            return buffer;
        }

        return format.SampleRate == NetworkSampleRate
            ? monoPcm16
            : ResamplePcm16Mono(monoPcm16, format.SampleRate, NetworkSampleRate);
    }

    private static byte[] ConvertFloatToPcm16Mono(byte[] buffer, int channels)
    {
        var frameSize = 4 * channels;
        var frameCount = buffer.Length / frameSize;
        var output = new byte[frameCount * 2];
        for (var frame = 0; frame < frameCount; frame++)
        {
            double sum = 0;
            var frameOffset = frame * frameSize;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += BitConverter.ToSingle(buffer, frameOffset + (channel * 4));
            }

            var mono = Math.Clamp(sum / channels, -1d, 1d);
            var sample = (short)Math.Clamp(mono * 32767d, short.MinValue, short.MaxValue);
            BitConverter.GetBytes(sample).CopyTo(output, frame * 2);
        }

        return output;
    }

    private static byte[] ConvertPcm16ToMono(byte[] buffer, int channels)
    {
        if (channels == 1)
        {
            return buffer;
        }

        var frameSize = 2 * channels;
        var frameCount = buffer.Length / frameSize;
        var output = new byte[frameCount * 2];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sum = 0;
            var frameOffset = frame * frameSize;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += BitConverter.ToInt16(buffer, frameOffset + (channel * 2));
            }

            var sample = (short)Math.Clamp(sum / channels, short.MinValue, short.MaxValue);
            BitConverter.GetBytes(sample).CopyTo(output, frame * 2);
        }

        return output;
    }

    private static byte[] ResamplePcm16Mono(byte[] buffer, int sourceSampleRate, int targetSampleRate)
    {
        if (sourceSampleRate <= 0 || targetSampleRate <= 0 || sourceSampleRate == targetSampleRate)
        {
            return buffer;
        }

        var sourceSamples = buffer.Length / 2;
        if (sourceSamples <= 1)
        {
            return buffer;
        }

        var targetSamples = Math.Max(1, (int)Math.Round(sourceSamples * (targetSampleRate / (double)sourceSampleRate)));
        var output = new byte[targetSamples * 2];
        var ratio = (sourceSamples - 1) / (double)Math.Max(1, targetSamples - 1);
        for (var i = 0; i < targetSamples; i++)
        {
            var sourcePosition = i * ratio;
            var sourceIndex = (int)sourcePosition;
            var fraction = sourcePosition - sourceIndex;
            var current = BitConverter.ToInt16(buffer, sourceIndex * 2);
            var nextIndex = Math.Min(sourceIndex + 1, sourceSamples - 1);
            var next = BitConverter.ToInt16(buffer, nextIndex * 2);
            var sample = (short)Math.Clamp(current + ((next - current) * fraction), short.MinValue, short.MaxValue);
            BitConverter.GetBytes(sample).CopyTo(output, i * 2);
        }

        return output;
    }
}
