using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;

namespace TanukiBCL.VoiceProbe;

internal sealed class AudioDeviceSession : IDisposable
{
    private static readonly WaveFormat CaptureFormat = new(48_000, 16, 1);
    private static readonly WaveFormat PlaybackFormat = new(48_000, 16, 2);
    private readonly WaveInEvent capture;
    private readonly WaveOutEvent playback;
    private readonly MixingSampleProvider playbackMixer;
    private readonly VolumeSampleProvider masterMix;
    private readonly Dictionary<string, PeerPlayback> peerPlayback = [];
    private readonly object playbackGate = new();
    private readonly Action<ReadOnlyMemory<byte>> onCaptured;
    private readonly Action<bool> onVadChanged;
    private readonly TanukiVoiceActivityDetector voiceDetector = new();
    private bool? lastVadState;
    private volatile bool microphoneMuted;
    private volatile bool deafened;
    private volatile float masterVolume = 1f;
    private volatile float microphoneGain = 1f;
    private volatile bool microphoneSensitivityEnabled;
    private volatile MicrophoneActivationMode activationMode;
    private volatile bool pushToTalkPressed;
    private bool disposed;

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int waveOutGetDevCaps(nuint deviceId, out WaveOutCapabilities capabilities, int capabilitiesSize);

    public AudioDeviceSession(
        int inputDevice,
        int outputDevice,
        Action<ReadOnlyMemory<byte>> onCaptured,
        Action<bool> onVadChanged)
    {
        ValidateDeviceNumbers(inputDevice, outputDevice);
        this.onCaptured = onCaptured;
        this.onVadChanged = onVadChanged;

        capture = new WaveInEvent
        {
            DeviceNumber = inputDevice,
            WaveFormat = CaptureFormat,
            BufferMilliseconds = 20,
            NumberOfBuffers = 3
        };
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                Console.Error.WriteLine($"マイク録音エラー: {args.Exception.Message}");
            }
        };

        playbackMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2))
        {
            ReadFully = true
        };
        masterMix = new VolumeSampleProvider(playbackMixer);
        playback = new WaveOutEvent
        {
            DeviceNumber = outputDevice,
            DesiredLatency = 100,
            NumberOfBuffers = 3
        };
        playback.Init(masterMix.ToWaveProvider());
    }

    public void Start()
    {
        playback.Play();
        capture.StartRecording();
        Console.WriteLine("実音声モード開始: マイク → Opus/WebRTC → 相手のスピーカー");
    }

    public void SetMicrophoneMuted(bool muted)
    {
        microphoneMuted = muted;
        if (muted)
        {
            lock (voiceDetector) voiceDetector.ResetActivity();
        }
        if (muted && lastVadState != false)
        {
            lastVadState = false;
            onVadChanged(false);
        }
    }

    public void SetDeafened(bool value)
    {
        deafened = value;
        masterMix.Volume = value ? 0f : masterVolume;
    }

    public void SetMasterVolume(double volumePercent)
    {
        masterVolume = (float)Math.Clamp(volumePercent / 100d, 0d, 2d);
        masterMix.Volume = deafened ? 0f : masterVolume;
    }

    public void SetMicrophoneGain(double gainPercent)
    {
        microphoneGain = (float)Math.Clamp(gainPercent / 100d, 0d, 3d);
    }

    public void SetMicrophoneSensitivity(bool enabled, double minimumNoiseLevel)
    {
        lock (voiceDetector)
        {
            voiceDetector.SetMinimumNoiseLevel(enabled ? minimumNoiseLevel : 0.15d);
        }
        microphoneSensitivityEnabled = enabled;
    }

    public void SetMicrophoneActivationMode(MicrophoneActivationMode mode)
    {
        activationMode = mode;
        pushToTalkPressed = false;
    }

    public void SetPushToTalkPressed(bool pressed) => pushToTalkPressed = pressed;

    public void SubmitPlayback(string peerId, ReadOnlySpan<short> stereoPcm)
    {
        if (disposed || stereoPcm.IsEmpty)
        {
            return;
        }

        var peer = GetOrCreatePeerPlayback(peerId);
        var bytes = new byte[stereoPcm.Length * sizeof(short)];
        Buffer.BlockCopy(stereoPcm.ToArray(), 0, bytes, 0, bytes.Length);
        peer.Buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void SetPeerMix(string peerId, PeerVoiceMix mix)
    {
        var peer = GetOrCreatePeerPlayback(peerId);
        peer.Volume.Volume = (float)Math.Clamp(mix.Gain, 0d, 2d);
        peer.Panning.Pan = (float)Math.Clamp(mix.Pan, -1d, 1d);
        peer.Muffle.Enabled = mix.Muffled;
        peer.RadioHighPass.Enabled = mix.RadioHighPass;
        peer.RadioEcho.Enabled = mix.RadioEcho;
    }

    public void RemovePeer(string peerId)
    {
        lock (playbackGate)
        {
            if (peerPlayback.Remove(peerId, out var peer))
            {
                playbackMixer.RemoveMixerInput(peer.RadioEcho);
            }
        }
    }

    private PeerPlayback GetOrCreatePeerPlayback(string peerId)
    {
        lock (playbackGate)
        {
            if (peerPlayback.TryGetValue(peerId, out var existing))
            {
                return existing;
            }

            var buffer = new BufferedWaveProvider(PlaybackFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(400),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            var mono = new StereoToMonoSampleProvider(buffer.ToSampleProvider())
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
            var muffle = new VentMuffleSampleProvider(mono);
            var radioHighPass = new RadioHighPassSampleProvider(muffle);
            var panning = new PanningSampleProvider(radioHighPass);
            var volume = new VolumeSampleProvider(panning);
            var radioEcho = new RadioEchoSampleProvider(volume);
            var created = new PeerPlayback(buffer, muffle, radioHighPass, panning, volume, radioEcho);
            peerPlayback[peerId] = created;
            playbackMixer.AddMixerInput(radioEcho);
            return created;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (disposed || args.BytesRecorded <= 0)
        {
            return;
        }

        var buffer = args.Buffer.AsMemory(0, args.BytesRecorded).ToArray();
        var sensitivityEnabled = microphoneSensitivityEnabled;
        // The upstream VAD analyses the raw microphone before output gain.
        bool talking;
        lock (voiceDetector)
        {
            talking = voiceDetector.ProcessPcm16(buffer);
        }
        var gain = microphoneGain;
        if (gain != 1f)
        {
            for (var index = 0; index + 1 < buffer.Length; index += sizeof(short))
            {
                var sample = BitConverter.ToInt16(buffer, index);
                var adjusted = (short)Math.Clamp((int)Math.Round(sample * gain), short.MinValue, short.MaxValue);
                BitConverter.TryWriteBytes(buffer.AsSpan(index, sizeof(short)), adjusted);
            }
        }
        var audioAllowed = MicrophoneActivationPolicy.AllowsAudio(activationMode, pushToTalkPressed, microphoneMuted);
        if (!audioAllowed || (sensitivityEnabled && !talking))
        {
            Array.Clear(buffer);
        }
        var transmittedTalking = audioAllowed && talking;
        if (lastVadState != transmittedTalking)
        {
            lastVadState = transmittedTalking;
            onVadChanged(transmittedTalking);
        }

        onCaptured(buffer);
    }

    public static void PrintDevices()
    {
        Console.WriteLine("入力デバイス:");
        for (var index = 0; index < WaveInEvent.DeviceCount; index++)
        {
            Console.WriteLine($"  {index}: {WaveInEvent.GetCapabilities(index).ProductName}");
        }

        if (WaveInEvent.DeviceCount == 0)
        {
            Console.WriteLine("  （見つかりません）");
        }

        Console.WriteLine("出力デバイス:");
        var outputDeviceCount = checked((int)waveOutGetNumDevs());
        for (var index = 0; index < outputDeviceCount; index++)
        {
            var result = waveOutGetDevCaps(
                checked((nuint)index),
                out var capabilities,
                Marshal.SizeOf<WaveOutCapabilities>());
            Console.WriteLine(result == 0
                ? $"  {index}: {capabilities.ProductName}"
                : $"  {index}: （名前を取得できません: WinMM {result}）");
        }

        if (outputDeviceCount == 0)
        {
            Console.WriteLine("  （見つかりません）");
        }
    }

    public static IReadOnlyList<AudioDeviceInfo> GetInputDevices() =>
        Enumerable.Range(0, WaveInEvent.DeviceCount)
            .Select(index => new AudioDeviceInfo(index, WaveInEvent.GetCapabilities(index).ProductName))
            .ToArray();

    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        var count = checked((int)waveOutGetNumDevs());
        for (var index = 0; index < count; index++)
        {
            var result = waveOutGetDevCaps(
                checked((nuint)index),
                out var capabilities,
                Marshal.SizeOf<WaveOutCapabilities>());
            devices.Add(new AudioDeviceInfo(index, result == 0 ? capabilities.ProductName : $"出力デバイス {index}"));
        }
        return devices;
    }

    private static void ValidateDeviceNumbers(int inputDevice, int outputDevice)
    {
        if (inputDevice < 0 || inputDevice >= WaveInEvent.DeviceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(inputDevice), $"入力デバイス {inputDevice} は存在しません。");
        }

        var outputDeviceCount = checked((int)waveOutGetNumDevs());
        if (outputDevice < 0 || outputDevice >= outputDeviceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(outputDevice), $"出力デバイス {outputDevice} は存在しません。");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        capture.StopRecording();
        playback.Stop();
        capture.Dispose();
        playback.Dispose();
    }

    private sealed record PeerPlayback(
        BufferedWaveProvider Buffer,
        VentMuffleSampleProvider Muffle,
        RadioHighPassSampleProvider RadioHighPass,
        PanningSampleProvider Panning,
        VolumeSampleProvider Volume,
        RadioEchoSampleProvider RadioEcho);

}

internal sealed record AudioDeviceInfo(int Id, string Name)
{
    public override string ToString() => $"{Id}: {Name}";
}
