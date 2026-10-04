using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;

namespace TanukiBCL.VoiceProbe;

internal sealed class AudioDeviceSession : IDisposable
{
    private static readonly WaveFormat PlaybackFormat = new(48_000, 16, 2);
    private WaveInEvent capture;
    private readonly int inputDevice;
    private readonly WaveOutEvent playback;
    private readonly MixingSampleProvider playbackMixer;
    private readonly VolumeSampleProvider masterMix;
    private readonly Dictionary<string, PeerPlayback> peerPlayback = [];
    private readonly object playbackGate = new();
    private readonly Action<ReadOnlyMemory<byte>> onCaptured;
    private readonly Action<bool> onVadChanged;
    private readonly MicrophoneProcessor microphoneProcessor;
    private MicrophoneCaptureConverter captureConverter;
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
        Action<bool> onVadChanged,
        bool echoCancellation = true,
        bool noiseSuppression = true,
        bool autoGainControl = false,
        bool oldSampleDebug = false)
    {
        ValidateDeviceNumbers(inputDevice, outputDevice);
        this.inputDevice = inputDevice;
        this.onCaptured = onCaptured;
        this.onVadChanged = onVadChanged;
        microphoneProcessor = new MicrophoneProcessor(echoCancellation, noiseSuppression, autoGainControl);
        var inputRate = MicrophoneCaptureRate.Choose(inputDevice, oldSampleDebug);
        captureConverter = new MicrophoneCaptureConverter(inputRate);

        try
        {
            capture = CreateCapture(inputRate);

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
            playback.Init(new RenderReferenceSampleProvider(masterMix, microphoneProcessor).ToWaveProvider());
        }
        catch
        {
            // Init can fail after the DSP and microphone objects were created
            // (for example, the selected speaker disappears during reconnect).
            try { capture?.Dispose(); }
            finally
            {
                try { playback?.Dispose(); }
                finally { microphoneProcessor.Dispose(); }
            }
            throw;
        }
    }

    public void Start()
    {
        playback.Play();
        try { capture.StartRecording(); }
        catch (NAudio.MmException error) when (capture.WaveFormat.SampleRate != MicrophoneCaptureRate.OutputRate &&
                                             error.Result == NAudio.MmResult.WaveBadFormat)
        {
            // The endpoint mix rate is advisory; a legacy WinMM driver may
            // refuse that PCM16 mono format even though its mix rate matches.
            Console.Error.WriteLine($"マイクの既定レートをWinMMが拒否 ({error.Message})。48 kHzで再試行します。");
            capture.DataAvailable -= OnDataAvailable;
            capture.Dispose();
            capture = CreateCapture(MicrophoneCaptureRate.OutputRate);
            captureConverter = new MicrophoneCaptureConverter(MicrophoneCaptureRate.OutputRate);
            capture.StartRecording();
        }
        Console.WriteLine($"実音声モード開始: マイク {capture.WaveFormat.SampleRate} Hz → Opus 48 kHz/WebRTC → 相手のスピーカー");
    }

    internal int CaptureSampleRate => capture.WaveFormat.SampleRate;

    private WaveInEvent CreateCapture(int rate)
    {
        var device = new WaveInEvent
        {
            DeviceNumber = inputDevice,
            WaveFormat = new WaveFormat(rate, 16, 1),
            BufferMilliseconds = 20,
            NumberOfBuffers = 3
        };
        device.DataAvailable += OnDataAvailable;
        device.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null)
                Console.Error.WriteLine($"マイク録音エラー: {args.Exception.Message}");
        };
        return device;
    }

    public void SetMicrophoneMuted(bool muted)
    {
        microphoneMuted = muted;
        if (muted) ResetVoiceActivity();
    }

    public void SetDeafened(bool value)
    {
        deafened = value;
        if (value)
        {
            pushToTalkPressed = false;
            ResetVoiceActivity();
        }
        masterMix.Volume = value ? 0f : masterVolume;
    }

    private void ResetVoiceActivity()
    {
        lock (voiceDetector) voiceDetector.ResetActivity();
        if (lastVadState == false) return;
        lastVadState = false;
        onVadChanged(false);
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

    public void SetPeerMix(string peerId, PeerVoiceMix mix, NosSizeVoiceEffect? nosSizeEffect = null)
    {
        var peer = GetOrCreatePeerPlayback(peerId);
        peer.Volume.Volume = (float)Math.Clamp(mix.Gain, 0d, 2d);
        peer.Panning.Pan = (float)Math.Clamp(mix.Pan, -1d, 1d);
        peer.Muffle.Enabled = mix.Muffled;
        peer.RadioHighPass.Enabled = mix.RadioHighPass;
        peer.RadioEcho.Enabled = mix.RadioEcho;
        peer.CameraMuffle.Enabled = mix.CameraMuffled;
        peer.GhostReverb.Enabled = mix.Reverb && mix.Audible;
        peer.NosSizeEffect.SetEffect(nosSizeEffect);
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
            var nosSizeEffect = new NosSizeVoiceSampleProvider(mono);
            var muffle = new VentMuffleSampleProvider(nosSizeEffect);
            var radioHighPass = new RadioHighPassSampleProvider(muffle);
            var cameraMuffle = new CameraMuffleSampleProvider(radioHighPass);
            var panning = new PanningSampleProvider(cameraMuffle);
            var volume = new VolumeSampleProvider(panning);
            var ghostReverb = new GhostReverbSampleProvider(volume);
            var radioEcho = new RadioEchoSampleProvider(ghostReverb);
            var created = new PeerPlayback(buffer, nosSizeEffect, muffle, radioHighPass,
                cameraMuffle, panning, volume, ghostReverb, radioEcho);
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

        captureConverter.Push(args.Buffer.AsSpan(0, args.BytesRecorded), ProcessCaptureFrame);
    }

    private void ProcessCaptureFrame(byte[] buffer)
    {
        if (disposed) return;
        microphoneProcessor.ProcessCapture(buffer);
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
        var audioAllowed = MicrophoneActivationPolicy.AllowsAudio(activationMode, pushToTalkPressed,
            microphoneMuted, deafened);
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
            Console.WriteLine($"  {index}: {WaveInEvent.GetCapabilities(index).ProductName} " +
                $"(既定 {MicrophoneCaptureRate.Choose(index, false)} Hz)");
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
        capture.DataAvailable -= OnDataAvailable;
        try { capture.StopRecording(); }
        finally
        {
            try { playback.Stop(); }
            finally
            {
                try { capture.Dispose(); }
                finally
                {
                    try { playback.Dispose(); }
                    finally { microphoneProcessor.Dispose(); }
                }
            }
        }
    }

    private sealed class RenderReferenceSampleProvider(
        ISampleProvider source, MicrophoneProcessor processor) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            processor.SubmitPlayback(buffer, offset, read);
            return read;
        }
    }

    private sealed record PeerPlayback(
        BufferedWaveProvider Buffer,
        NosSizeVoiceSampleProvider NosSizeEffect,
        VentMuffleSampleProvider Muffle,
        RadioHighPassSampleProvider RadioHighPass,
        CameraMuffleSampleProvider CameraMuffle,
        PanningSampleProvider Panning,
        VolumeSampleProvider Volume,
        GhostReverbSampleProvider GhostReverb,
        RadioEchoSampleProvider RadioEcho);

}

// WinMM normally delivers 20 ms blocks, but stopped/irregular callbacks may be
// shorter. Retain their bytes so DSP, VAD and Opus see complete frames only.
internal sealed class Pcm16CaptureFramer
{
    private byte[] pending = new byte[MicrophoneProcessor.BytesPerFrame];
    private int pendingLength;

    public void Push(ReadOnlySpan<byte> input, Action<byte[]> onFrame)
    {
        while (!input.IsEmpty)
        {
            var copied = Math.Min(input.Length, pending.Length - pendingLength);
            input[..copied].CopyTo(pending.AsSpan(pendingLength));
            pendingLength += copied;
            input = input[copied..];
            if (pendingLength != pending.Length) continue;
            var complete = pending;
            pending = new byte[MicrophoneProcessor.BytesPerFrame];
            pendingLength = 0;
            onFrame(complete);
        }
    }
}

internal sealed record AudioDeviceInfo(int Id, string Name)
{
    public override string ToString() => $"{Id}: {Name}";
}
