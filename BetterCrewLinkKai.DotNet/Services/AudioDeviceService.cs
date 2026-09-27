using BetterCrewLinkKai.DotNet.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BetterCrewLinkKai.DotNet.Services;

public sealed class AudioDeviceService : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private IWaveIn? microphoneCapture;
    private IWaveIn? voiceEffectCapture;
    private IWavePlayer? voiceEffectOutput;
    private BufferedWaveProvider? voiceEffectBuffer;

    public event EventHandler<double>? MicrophoneLevelChanged;

    public IReadOnlyList<AudioDeviceInfo> GetMicrophones()
    {
        return GetDevices(DataFlow.Capture);
    }

    public IReadOnlyList<AudioDeviceInfo> GetSpeakers()
    {
        return GetDevices(DataFlow.Render);
    }

    public async Task PlaySpeakerTestAsync(string deviceId, double volume, CancellationToken cancellationToken = default)
    {
        var device = GetDevice(DataFlow.Render, deviceId);
        using var output = device is null
            ? new WasapiOut(AudioClientShareMode.Shared, 80)
            : new WasapiOut(device, AudioClientShareMode.Shared, false, 80);

        var signal = new SignalGenerator(44100, 2)
        {
            Gain = Math.Clamp(volume / 100d, 0d, 2d) * 0.18,
            Frequency = 880,
            Type = SignalGeneratorType.Sin
        };
        var sample = new OffsetSampleProvider(signal)
        {
            Take = TimeSpan.FromMilliseconds(650)
        };

        output.Init(sample);
        output.Play();

        while (output.PlaybackState == PlaybackState.Playing && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(30, cancellationToken);
        }
    }

    public void StartVoiceEffectTest(string microphoneId, string speakerId, double strength, double volume)
    {
        StopVoiceEffectTest();

        voiceEffectCapture = CreateMicrophoneCapture(microphoneId);
        voiceEffectBuffer = new BufferedWaveProvider(voiceEffectCapture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(250)
        };

        voiceEffectCapture.DataAvailable += OnVoiceEffectDataAvailable;
        voiceEffectOutput = CreateOutput(speakerId);
        voiceEffectOutput.Init(new VoiceEffectWaveProvider(voiceEffectBuffer, strength, volume));
        voiceEffectOutput.Play();
        voiceEffectCapture.StartRecording();
    }

    public void StopVoiceEffectTest()
    {
        if (voiceEffectCapture is not null)
        {
            voiceEffectCapture.DataAvailable -= OnVoiceEffectDataAvailable;
            voiceEffectCapture.StopRecording();
            voiceEffectCapture.Dispose();
            voiceEffectCapture = null;
        }

        voiceEffectOutput?.Stop();
        voiceEffectOutput?.Dispose();
        voiceEffectOutput = null;
        voiceEffectBuffer = null;
    }

    public void StartMicrophoneMonitor(string deviceId)
    {
        StopMicrophoneMonitor();

        microphoneCapture = CreateMicrophoneCapture(deviceId);
        microphoneCapture.DataAvailable += OnMicrophoneDataAvailable;
        microphoneCapture.RecordingStopped += (_, _) => MicrophoneLevelChanged?.Invoke(this, 0);
        microphoneCapture.StartRecording();
    }

    public void StopMicrophoneMonitor()
    {
        if (microphoneCapture is null)
        {
            return;
        }

        microphoneCapture.DataAvailable -= OnMicrophoneDataAvailable;
        microphoneCapture.StopRecording();
        microphoneCapture.Dispose();
        microphoneCapture = null;
        MicrophoneLevelChanged?.Invoke(this, 0);
    }

    public void Dispose()
    {
        StopVoiceEffectTest();
        StopMicrophoneMonitor();
        enumerator.Dispose();
    }

    private IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        var devices = new List<AudioDeviceInfo>
        {
            new() { Id = "Default", Name = "デフォルト" }
        };

        devices.AddRange(enumerator
            .EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(static device => new AudioDeviceInfo
            {
                Id = device.ID,
                Name = device.FriendlyName
            }));

        return devices;
    }

    private MMDevice? GetDevice(DataFlow flow, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            string.Equals(deviceId, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return enumerator
            .EnumerateAudioEndPoints(flow, DeviceState.Active)
            .FirstOrDefault(device => string.Equals(device.ID, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    private IWaveIn CreateMicrophoneCapture(string deviceId)
    {
        try
        {
            var device = GetDevice(DataFlow.Capture, deviceId);
            var capture = device is null
                ? new WasapiCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                : new WasapiCapture(device);
            capture.ShareMode = AudioClientShareMode.Shared;
            return capture;
        }
        catch
        {
            return new WaveInEvent
            {
                DeviceNumber = 0,
                BufferMilliseconds = 50,
                WaveFormat = new WaveFormat(44100, 16, 1)
            };
        }
    }

    private IWavePlayer CreateOutput(string deviceId)
    {
        var device = GetDevice(DataFlow.Render, deviceId);
        return device is null
            ? new WasapiOut(AudioClientShareMode.Shared, 80)
            : new WasapiOut(device, AudioClientShareMode.Shared, false, 80);
    }

    private void OnVoiceEffectDataAvailable(object? sender, WaveInEventArgs e)
    {
        voiceEffectBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnMicrophoneDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            MicrophoneLevelChanged?.Invoke(this, 0);
            return;
        }

        var waveFormat = microphoneCapture?.WaveFormat;
        var bytesPerSample = waveFormat?.BitsPerSample / 8 ?? 2;
        if (bytesPerSample <= 0)
        {
            bytesPerSample = 2;
        }

        var sampleCount = e.BytesRecorded / bytesPerSample;
        if (sampleCount <= 0)
        {
            MicrophoneLevelChanged?.Invoke(this, 0);
            return;
        }

        double total = 0;
        if (bytesPerSample == 4 && waveFormat?.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            for (var i = 0; i + 3 < e.BytesRecorded; i += 4)
            {
                var sample = BitConverter.ToSingle(e.Buffer, i);
                total += sample * sample;
            }
        }
        else if (bytesPerSample == 4)
        {
            for (var i = 0; i + 3 < e.BytesRecorded; i += 4)
            {
                var sample = BitConverter.ToInt32(e.Buffer, i) / 2147483648d;
                total += sample * sample;
            }
        }
        else if (bytesPerSample == 3)
        {
            for (var i = 0; i + 2 < e.BytesRecorded; i += 3)
            {
                var sample = (e.Buffer[i] | (e.Buffer[i + 1] << 8) | (e.Buffer[i + 2] << 16));
                if ((sample & 0x800000) != 0)
                {
                    sample |= unchecked((int)0xff000000);
                }

                total += Math.Pow(sample / 8388608d, 2);
            }
        }
        else
        {
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                var sample = BitConverter.ToInt16(e.Buffer, i) / 32768d;
                total += sample * sample;
            }
        }

        var rms = Math.Sqrt(total / sampleCount);
        MicrophoneLevelChanged?.Invoke(this, Math.Clamp(rms * 220, 0, 100));
    }

    private sealed class VoiceEffectWaveProvider : IWaveProvider
    {
        private readonly IWaveProvider source;
        private readonly double strength;
        private readonly double outputScale;
        private double lowPass;

        public VoiceEffectWaveProvider(IWaveProvider source, double strength, double volume)
        {
            this.source = source;
            this.strength = Math.Clamp(strength, 0, 200) / 100d;
            outputScale = Math.Clamp(volume / 100d, 0d, 2d);
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (read <= 0)
            {
                return read;
            }

            var bytesPerSample = WaveFormat.BitsPerSample / 8;
            if (WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
            {
                ProcessFloat(buffer, offset, read);
            }
            else if (bytesPerSample == 2)
            {
                ProcessPcm16(buffer, offset, read);
            }

            return read;
        }

        private double ApplyEffect(double sample)
        {
            lowPass += 0.08 * (sample - lowPass);
            var filtered = sample - lowPass;
            var wet = Math.Tanh(filtered * (1.6 + strength * 1.8));
            var dryMix = 1 - Math.Min(0.95, strength * 0.95);
            var wetMix = Math.Min(1.45, strength * 1.45);
            return Math.Clamp(((sample * dryMix) + (wet * wetMix)) * outputScale, -1, 1);
        }

        private void ProcessFloat(byte[] buffer, int offset, int count)
        {
            for (var i = offset; i + 3 < offset + count; i += 4)
            {
                var sample = BitConverter.ToSingle(buffer, i);
                var processed = (float)ApplyEffect(sample);
                BitConverter.GetBytes(processed).CopyTo(buffer, i);
            }
        }

        private void ProcessPcm16(byte[] buffer, int offset, int count)
        {
            for (var i = offset; i + 1 < offset + count; i += 2)
            {
                var sample = BitConverter.ToInt16(buffer, i) / 32768d;
                var processed = (short)Math.Clamp(ApplyEffect(sample) * 32767, short.MinValue, short.MaxValue);
                BitConverter.GetBytes(processed).CopyTo(buffer, i);
            }
        }
    }
}
