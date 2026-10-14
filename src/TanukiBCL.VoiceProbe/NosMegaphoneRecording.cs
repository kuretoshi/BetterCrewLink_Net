using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

// A listener records the decoded voice before spatial attenuation, then plays
// it through a separate mixer input when NoS reports that the beam fired.
// At most ten seconds of 48 kHz mono audio are retained, only in memory.
internal sealed class NosMegaphoneRecording : ISampleProvider
{
    private const int SampleRate = 48_000;
    private const int MaxSamples = SampleRate * 10;
    private const int ChunkSamples = SampleRate / 5;
    private const int DelaySamples = SampleRate * 24 / 100;
    private const int TailSamples = SampleRate * 24 / 10;
    private readonly object gate = new();
    private List<float[]>? recording;
    private int recordedSamples;
    private List<float[]>? playback;
    private int playbackSamples;
    private int playbackPosition;
    private float[]? delay;
    private int delayPosition;
    private int tailRemaining;
    private float lowpass;
    private long shot;
    private long fired;
    private float volume = 1f;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

    internal int RecordedSamples { get { lock (gate) return recordedSamples; } }
    internal int BufferedCapacitySamples
    {
        get { lock (gate) return (recording ?? playback)?.Count * ChunkSamples ?? 0; }
    }
    internal bool IsPlaying { get { lock (gate) return playback is not null; } }

    public void Update(NosMegaphoneState? entry, bool active, double playbackVolume)
    {
        lock (gate)
        {
            volume = double.IsFinite(playbackVolume)
                ? (float)Math.Clamp(playbackVolume, 0d, 2d) : 0f;
            if (!active)
            {
                CancelCore();
                return;
            }
            if (entry?.Phase is null || entry.ShotId is not > 0)
            {
                recording = null;
                recordedSamples = 0;
                return; // Playback may finish after the beam disappears.
            }
            if (entry.ShotId.Value != shot)
            {
                CancelCore();
                shot = entry.ShotId.Value;
            }
            if (entry.Phase == "charging")
            {
                if (fired != shot) recording ??= [];
                return;
            }
            if (entry.Phase != "firing" || fired == shot) return;
            fired = shot;
            if (recording is null || recordedSamples == 0) return;
            playback = recording;
            playbackSamples = recordedSamples;
            playbackPosition = 0;
            recording = null;
            recordedSamples = 0;
            tailRemaining = entry.VoiceEffect == false ? 0 : TailSamples;
            delay = tailRemaining > 0 ? new float[DelaySamples] : null;
            delayPosition = 0;
            lowpass = 0f;
        }
    }

    public void RecordMonoPcm16(ReadOnlySpan<byte> pcm)
    {
        lock (gate)
        {
            if (recording is null) return;
            var count = Math.Min(pcm.Length / sizeof(short), MaxSamples - recordedSamples);
            for (var index = 0; index < count; index++)
                AppendSample(BitConverter.ToInt16(pcm.Slice(index * 2, 2)) / 32768f);
        }
    }

    public void RecordStereoPcm16(ReadOnlySpan<short> pcm)
    {
        lock (gate)
        {
            if (recording is null) return;
            var count = Math.Min(pcm.Length / 2, MaxSamples - recordedSamples);
            for (var index = 0; index < count; index++)
                AppendSample((pcm[index * 2] + pcm[index * 2 + 1]) / 65536f);
        }
    }

    private void AppendSample(float sample)
    {
        if (recordedSamples % ChunkSamples == 0) recording!.Add(new float[ChunkSamples]);
        recording![^1][recordedSamples % ChunkSamples] = sample;
        recordedSamples++;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        lock (gate)
        {
            if (playback is null) return count;
            for (var index = 0; index + 1 < count; index += 2)
            {
                var sample = playbackPosition < playbackSamples
                    ? ReadPlaybackSample(playbackPosition++) : 0f;
                var mixed = sample * 0.7f;
                if (delay is not null)
                {
                    var delayed = delay[delayPosition];
                    delay[delayPosition] = sample + delayed * 0.48f;
                    delayPosition = (delayPosition + 1) % delay.Length;
                    lowpass += 0.3f * (delayed - lowpass);
                    var envelope = playbackPosition < playbackSamples ? 1f :
                        Math.Max(0f, (float)tailRemaining / TailSamples);
                    mixed += 0.3f * lowpass * envelope;
                }
                if (playbackPosition >= playbackSamples && tailRemaining > 0) tailRemaining--;
                mixed *= volume;
                buffer[offset + index] = mixed;
                buffer[offset + index + 1] = mixed;
                if (playbackPosition < playbackSamples || tailRemaining > 0) continue;
                playback = null;
                delay = null;
                break;
            }
        }
        return count;
    }

    private float ReadPlaybackSample(int position) =>
        playback![position / ChunkSamples][position % ChunkSamples];

    public void Cancel()
    {
        lock (gate) CancelCore();
    }

    private void CancelCore()
    {
        recording = null;
        recordedSamples = 0;
        playback = null;
        playbackSamples = 0;
        playbackPosition = 0;
        delay = null;
        delayPosition = 0;
        tailRemaining = 0;
        lowpass = 0f;
        shot = 0;
        fired = 0;
    }
}
