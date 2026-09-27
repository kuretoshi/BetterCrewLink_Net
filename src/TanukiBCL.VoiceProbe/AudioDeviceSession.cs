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
    private readonly Dictionary<string, PeerPlayback> peerPlayback = [];
    private readonly object playbackGate = new();
    private readonly Action<ReadOnlyMemory<byte>> onCaptured;
    private readonly Action<bool> onVadChanged;
    private bool? lastVadState;
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
        playback = new WaveOutEvent
        {
            DeviceNumber = outputDevice,
            DesiredLatency = 100,
            NumberOfBuffers = 3
        };
        playback.Init(playbackMixer.ToWaveProvider());
    }

    public void Start()
    {
        playback.Play();
        capture.StartRecording();
        Console.WriteLine("実音声モード開始: マイク → Opus/WebRTC → 相手のスピーカー");
    }

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
    }

    public void RemovePeer(string peerId)
    {
        lock (playbackGate)
        {
            if (peerPlayback.Remove(peerId, out var peer))
            {
                playbackMixer.RemoveMixerInput(peer.Volume);
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
            var panning = new PanningSampleProvider(mono);
            var volume = new VolumeSampleProvider(panning);
            var created = new PeerPlayback(buffer, panning, volume);
            peerPlayback[peerId] = created;
            playbackMixer.AddMixerInput(volume);
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
        var talking = CalculateRms(buffer) >= 0.015d;
        if (lastVadState != talking)
        {
            lastVadState = talking;
            onVadChanged(talking);
        }

        onCaptured(buffer);
    }

    private static double CalculateRms(byte[] pcm16)
    {
        if (pcm16.Length < 2)
        {
            return 0;
        }

        double sumSquares = 0;
        var samples = pcm16.Length / 2;
        for (var index = 0; index < samples; index++)
        {
            var sample = BitConverter.ToInt16(pcm16, index * 2) / 32768d;
            sumSquares += sample * sample;
        }

        return Math.Sqrt(sumSquares / samples);
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
        PanningSampleProvider Panning,
        VolumeSampleProvider Volume);
}
