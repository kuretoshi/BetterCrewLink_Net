using NAudio.Wave;
using NAudio.Dsp;
using TanukiBCL.VoiceProbe.GameMemory;

namespace TanukiBCL.VoiceProbe;

public sealed record RokuroVoiceSettings(int HeadVolume = 100, int BodyVolume = 35, int BodyEcho = 50)
{
    public RokuroVoiceSettings Normalize() => new(Math.Clamp(HeadVolume, 0, 100), Math.Clamp(BodyVolume, 0, 100), Math.Clamp(BodyEcho, 0, 100));
}

internal sealed record RokuroBodyMix(double Gain, double Pan, int Echo, double Cutoff = 1800);

internal static class RokuroVoicePolicy
{
    internal static PeerVoiceMix Apply(AmongUsState state, Player me, Player other, SpatialVoiceSettings spatial,
        PeerVoiceMix head, RokuroVoiceSettings settings, bool enabled, bool radio, bool airshipFallback)
    {
        var speaker = other.NosPlayer;
        if (!enabled || state.Mod != AmongUsModType.NebulaOnTheShip || state.GameState != GameState.Tasks ||
            me.IsDead || other.IsDead || other.Disconnected || other.IsDummy || radio || airshipFallback || state.AirshipMeetingByOutfit ||
            speaker is not { BodyType: 3, NeckLength: > 0 } ||
            !double.IsFinite(speaker.NeckLength.Value) || !double.IsFinite(speaker.SpeakerPositionX) || !double.IsFinite(speaker.SpeakerPositionY) ||
            !double.IsFinite(other.X) || !double.IsFinite(other.Y) ||
            Math.Sqrt(Math.Pow(other.X - speaker.SpeakerPositionX, 2) + Math.Pow(other.Y - speaker.SpeakerPositionY, 2)) < .05) return head;
        var body = SpatialVoicePolicy.Calculate(state, me, other, spatial, rokuroBodyOrigin: true);
        var levels = settings.Normalize();
        return head with { Gain = head.Gain * levels.HeadVolume / 100d,
            Reason = !head.Audible && body.Audible ? "rokuro-body" : head.Reason,
            RokuroBody = new(body.Gain * levels.BodyVolume / 100d, body.Pan, levels.BodyEcho,
                body.FixerLowpass ? 1200 : 1800) };
    }
}

// Capture only while two-source playback is active. The network buffer is consumed once.
internal sealed class RokuroVoiceTap(ISampleProvider source) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;
    public volatile bool Capture;
    internal float[] Samples = [];
    internal int Count;
    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        Count = Capture ? read : 0;
        if (!Capture) return read;
        if (Samples.Length < read) Samples = new float[read];
        Array.Copy(buffer, offset, Samples, 0, read);
        return read;
    }
}

internal sealed class RokuroVoiceSampleProvider(ISampleProvider head, RokuroVoiceTap tap) : ISampleProvider
{
    private RokuroBodyMix? requested;
    private float[]? history;
    private int position;
    private BiQuadFilter? lowpass;
    private double cutoff;
    private double gain, echo;
    public WaveFormat WaveFormat => head.WaveFormat;
    internal void SetMix(RokuroBodyMix? mix)
    {
        Volatile.Write(ref requested, mix);
        tap.Capture = mix is not null && mix.Gain > 0;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = head.Read(buffer, offset, count);
        var mix = Volatile.Read(ref requested);
        if (mix is null || mix.Gain <= 0)
        {
            history = null; lowpass = null; gain = 0; echo = 0; position = 0;
            return read;
        }
        history ??= new float[(int)Math.Round(WaveFormat.SampleRate * .18)];
        if (lowpass is null || cutoff != mix.Cutoff)
        {
            cutoff = mix.Cutoff;
            lowpass = BiQuadFilter.LowPassFilter(WaveFormat.SampleRate, (float)cutoff, .7071f);
        }
        var pan = Math.Clamp(mix.Pan, -1, 1);
        var left = Math.Cos((pan + 1) * Math.PI / 4);
        var right = Math.Sin((pan + 1) * Math.PI / 4);
        var smoothing = 1 - Math.Exp(-1d / (WaveFormat.SampleRate * .01));
        for (var frame = 0; frame < read / 2; frame++)
        {
            gain += (mix.Gain - gain) * smoothing;
            echo += (Math.Clamp(mix.Echo, 0, 100) / 100d - echo) * smoothing;
            var input = lowpass.Transform(frame < tap.Count ? tap.Samples[frame] : 0) * gain;
            var delayed = history[position];
            history[position] = (float)(input + delayed * .3 * echo);
            var body = input * (1 - .35 * echo) + delayed * .5 * echo;
            var index = offset + frame * 2;
            buffer[index] = EffectPeakSafety.Apply(buffer[index] + body * left);
            buffer[index + 1] = EffectPeakSafety.Apply(buffer[index + 1] + body * right);
            if (++position == history.Length) position = 0;
        }
        return read;
    }
}
