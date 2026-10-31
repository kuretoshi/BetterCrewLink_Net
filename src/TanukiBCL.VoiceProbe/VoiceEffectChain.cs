using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TanukiBCL.VoiceProbe;

// Live playback and the microphone preview use the same pitch, room and echo path.
internal sealed class VoiceEffectChain
{
    private readonly TunedVoiceSampleProvider tone;
    private readonly EffectPeakSafetySampleProvider safety;
    private volatile bool requestedProtection;
    internal GhostReverbSampleProvider Reverb { get; }
    internal RadioEchoSampleProvider Echo { get; }
    internal ISampleProvider Output => safety;

    public VoiceEffectChain(ISampleProvider source, bool bibiriPreview = false,
        TunedVoiceSampleProvider? toneProvider = null)
    {
        // Live peers put tone before their mono spatial/filter path. Preview
        // and the independent stereo event playback own their input directly.
        tone = toneProvider ?? new TunedVoiceSampleProvider(source);
        var processed = toneProvider is null ? tone : source;
        var output = processed.WaveFormat.Channels == 1
            ? new MonoToStereoSampleProvider(processed) : (ISampleProvider)processed;
        Reverb = new GhostReverbSampleProvider(output) { SmoothTransitions = true };
        Echo = bibiriPreview
            ? new RadioEchoSampleProvider(Reverb, .3d, 1f, .65f, .56f, scaleFeedbackWithStrength: true) { SmoothTransitions = true }
            : new RadioEchoSampleProvider(Reverb, .18d, 1f, .12f, .22f) { SmoothTransitions = true };
        safety = new EffectPeakSafetySampleProvider(Echo, () => requestedProtection || tone.PitchPrepared ||
            tone.GritPrepared || Reverb.IsProcessing || Echo.IsProcessing);
    }

    public void SetTuning(VoiceEffectTuning tuning, double grit = 0, double extraReverb = 0,
        bool includeEcho = true, bool immediate = false)
    {
        tone.SetTuning(tuning, grit);
        requestedProtection = !tuning.IsNeutral || extraReverb > 0;
        Reverb.Strength = (float)Math.Clamp(Math.Max(tuning.Reverb / 100d, extraReverb), 0, 1);
        Reverb.Enabled = Reverb.Strength > 0;
        if (!Reverb.Enabled) Reverb.ReleaseInactiveHistory();
        Echo.Strength = (float)Math.Clamp(tuning.Echo / 100d, 0, 1);
        Echo.Enabled = includeEcho && Echo.Strength > 0;
        if (immediate)
        {
            tone.Reset();
            Reverb.ResetImmediately();
            Echo.Reset();
        }
    }

    internal static double GritAmount(VoiceEffectTuning tuning) =>
        Math.Max(Math.Abs(tuning.Pitch), Math.Max(tuning.Reverb, tuning.Echo)) / 100d;
}

// A stereo event recording needs independent pitch histories for its channels.
// Passing interleaved stereo through a mono shifter would alter pitch and pan.
internal sealed class TunedVoiceSampleProvider(ISampleProvider source) : ISampleProvider
{
    private sealed record Parameters(VoiceEffectTuning Tuning, double Grit);
    private Parameters requested = new(new VoiceEffectTuning(), 0);
    private SourceFilterDsp[]? pitch;
    private BerserkerDsp[]? grit;
    private const int WarmupFrames = 2048;
    private readonly int transitionFrames = Math.Max(1, source.WaveFormat.SampleRate / 100);
    private float[] channel = [];
    private float[] pitchBlend = [];
    private int warmup;
    private int resetRequested;
    private double pitchMix;
    private double lastPitch = 1;

    public void Reset() => Volatile.Write(ref resetRequested, 1);
    public WaveFormat WaveFormat => source.WaveFormat;
    internal bool PitchPrepared => pitch is not null;
    internal int PitchChannelCount => pitch?.Length ?? 0;
    internal bool GritPrepared => grit is not null;

    public void SetTuning(VoiceEffectTuning tuning, double gritAmount = 0) =>
        Volatile.Write(ref requested, new Parameters(tuning, Math.Clamp(gritAmount, 0, 1)));

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var settings = Volatile.Read(ref requested);
        var channels = WaveFormat.Channels;
        if (read % channels != 0) throw new InvalidOperationException("Incomplete audio frame.");
        if (Interlocked.Exchange(ref resetRequested, 0) != 0)
        {
            pitch = null;
            grit = null;
            pitchMix = 0;
            warmup = 0;
        }
        var wantsPitch = settings.Tuning.Pitch != 0;
        if (wantsPitch)
        {
            lastPitch = settings.Tuning.PitchRatio;
            if (pitch is null)
            {
                pitch = Enumerable.Range(0, channels).Select(_ => new SourceFilterDsp(WaveFormat.SampleRate)).ToArray();
                warmup = WarmupFrames;
            }
        }
        var gritAmount = settings.Tuning.IsNeutral ? 0 : settings.Grit;
        if (gritAmount > 0)
            grit ??= Enumerable.Range(0, channels).Select(_ => new BerserkerDsp(WaveFormat.SampleRate)).ToArray();
        if (pitch is null && grit is null) return read;
        var frames = read / channels;
        if (channel.Length < frames) channel = new float[frames];
        if (pitchBlend.Length < frames) pitchBlend = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            if (wantsPitch && warmup > 0) warmup--;
            else pitchMix = wantsPitch ? Math.Min(1, pitchMix + 1d / transitionFrames)
                : Math.Max(0, pitchMix - 1d / transitionFrames);
            pitchBlend[frame] = (float)pitchMix;
        }
        for (var index = 0; index < channels; index++)
        {
            for (var frame = 0; frame < frames; frame++) channel[frame] = buffer[offset + frame * channels + index];
            if (pitch is not null)
            {
                pitch[index].SetParameters(lastPitch, 1, 0);
                pitch[index].Process(channel, 0, frames);
                for (var frame = 0; frame < frames; frame++)
                {
                    var dry = buffer[offset + frame * channels + index];
                    channel[frame] = dry + (channel[frame] - dry) * pitchBlend[frame];
                }
            }
            grit?[index].Process(channel, 0, frames, gritAmount, smoothStrength: true);
            for (var frame = 0; frame < frames; frame++) buffer[offset + frame * channels + index] = channel[frame];
        }
        if (!wantsPitch && pitchMix == 0) { pitch = null; warmup = 0; }
        if (gritAmount == 0 && grit?.All(value => value.IsInactive) == true) grit = null;
        return read;
    }
}
