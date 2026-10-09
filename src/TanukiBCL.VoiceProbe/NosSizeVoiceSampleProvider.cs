using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// The v3.2.7 Web Audio effect pitch-shifts through two phase-offset delay
// windows. Reproducing that topology keeps pitch independent of frame rate.
internal sealed class NosSizeVoiceSampleProvider(ISampleProvider source) : ISampleProvider
{
    private const int SampleRate = 48_000;
    private const double UpWindowSeconds = 0.035d;
    private const double DownWindowSeconds = 0.08d;
    private readonly float[] delayLine = new float[8192];
    private readonly Biquad wetFilter = new();
    private readonly Biquad toneShelf = new();
    private SourceFilterDsp? sourceFilter;
    private BerserkerDsp? berserker;
    private NosSizeVoiceEffect? requestedEffect;
    private NosSizeVoiceEffect? appliedEffect;
    private int writeIndex;
    private double upPhase;
    private double downPhase;
    private double dryGain = 1d;
    private double wetGain;
    private double outputGain = 1d;
    private double pitchRate;
    private double toneDb;
    private bool pitchUp;
    private bool squaredFade;

    public WaveFormat WaveFormat => source.WaveFormat;

    public void SetEffect(NosSizeVoiceEffect? effect) => Volatile.Write(ref requestedEffect, effect);

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var effect = Volatile.Read(ref requestedEffect);
        if (effect is null)
        {
            appliedEffect = null;
            sourceFilter = null;
            berserker = null;
            return read;
        }
        if (effect != appliedEffect) Configure(effect);
        if (effect.Mode == NosSizeEffectMode.SourceFilter)
        {
            sourceFilter!.Process(buffer, offset, read);
            return read;
        }
        if (effect.Mode == NosSizeEffectMode.Berserker)
        {
            berserker!.Process(buffer, offset, read);
            return read;
        }

        for (var index = offset; index < offset + read; index++)
        {
            var dry = buffer[index];
            var wetInput = wetFilter.Process(dry);
            delayLine[writeIndex] = (float)wetInput;
            var shifted = wetGain > 0d ? WindowedPitchSample() : 0d;
            var combined = dry * dryGain + shifted * wetGain;
            buffer[index] = (float)(outputGain * (toneDb == 0d ? combined : toneShelf.Process(combined)));
            writeIndex = (writeIndex + 1) % delayLine.Length;
        }
        return read;
    }

    private void Configure(NosSizeVoiceEffect effect)
    {
        var previousMode = appliedEffect?.Mode;
        if (appliedEffect is null)
        {
            Array.Clear(delayLine);
            wetFilter.Reset();
            toneShelf.Reset();
            writeIndex = 0;
            upPhase = downPhase = 0d;
        }
        appliedEffect = effect;
        if (effect.Mode == NosSizeEffectMode.SourceFilter)
        {
            if (previousMode != effect.Mode) sourceFilter = new SourceFilterDsp(SampleRate);
            sourceFilter!.SetParameters(effect.Pitch, effect.Formant, effect.Squash);
            berserker = null;
            return;
        }
        if (effect.Mode == NosSizeEffectMode.Berserker)
        {
            if (previousMode != effect.Mode) berserker = new BerserkerDsp(SampleRate);
            sourceFilter = null;
            return;
        }
        sourceFilter = null;
        berserker = null;
        var strength = Math.Clamp(effect.Strength, 0d, 1d);
        toneDb = Math.Clamp(Math.Log(effect.ToneRate) / Math.Log(5d) * 6d, -6d, 6d);
        toneShelf.ConfigureLowShelf(400d, toneDb);
        switch (effect.Mode)
        {
            case NosSizeEffectMode.Squash:
                var progress = Math.Clamp(effect.Squash, 0d, 1d);
                wetFilter.ConfigureLowPass(6000d - progress * 5200d);
                dryGain = 1d - progress;
                wetGain = progress * 0.45d;
                outputGain = 1d - progress * 0.75d;
                pitchRate = 1d;
                pitchUp = true;
                squaredFade = false;
                break;
            case NosSizeEffectMode.PitchUp:
                wetFilter.ConfigureLowPass(12000d);
                dryGain = strength == 0d ? 1d : 0d;
                wetGain = strength == 0d ? 0d : 1d;
                outputGain = 1d;
                pitchRate = strength;
                pitchUp = true;
                squaredFade = false;
                break;
            case NosSizeEffectMode.Jumbo:
                wetFilter.ConfigureLowPass(12000d - strength * 8500d);
                dryGain = strength == 0d ? 1d : 0d;
                wetGain = strength == 0d ? 0d : 1d;
                outputGain = 1d;
                pitchRate = strength * 0.6d;
                pitchUp = false;
                squaredFade = true;
                break;
            case NosSizeEffectMode.ToneOnly:
                wetFilter.ConfigureLowPass(12000d);
                dryGain = 1d;
                wetGain = 0d;
                outputGain = 1d;
                pitchRate = 0d;
                pitchUp = true;
                squaredFade = false;
                break;
            case NosSizeEffectMode.Disguise:
                // Web Audio bandpass Q is linear (unlike low/high-pass Q).
                // Generic disguise keeps the 35 ms pitch-up modulators at 1x.
                wetFilter.ConfigureBandPass(1200d - strength * 350d, 1d + strength * 8d);
                dryGain = 1d - strength * 0.95d;
                wetGain = strength * 1.45d;
                outputGain = 1d;
                pitchRate = 1d;
                pitchUp = true;
                squaredFade = false;
                break;
        }
    }

    private double WindowedPitchSample()
    {
        var window = pitchUp ? UpWindowSeconds : DownWindowSeconds;
        var phase = pitchUp ? upPhase : downPhase;
        var second = (phase + 0.5d) % 1d;
        var a = ReadWindow(window, phase, phase);
        // v3.2.7 shares the same up-ramp delay buffer between A and B while
        // offsetting only B's fade; its down-ramp offsets both delay and fade.
        var b = ReadWindow(window, pitchUp ? phase : second, second);
        phase = (phase + pitchRate / (window * SampleRate)) % 1d;
        if (pitchUp) upPhase = phase;
        else downPhase = phase;
        return a + b;
    }

    private double ReadWindow(double window, double delayPhase, double fadePhase)
    {
        var delay = window * SampleRate * (pitchUp ? 1d - delayPhase : delayPhase);
        var fade = Math.Sin(Math.PI * fadePhase);
        if (squaredFade) fade *= fade;
        return ReadDelayed(delay) * fade;
    }

    private double ReadDelayed(double delaySamples)
    {
        var position = writeIndex - delaySamples;
        while (position < 0d) position += delayLine.Length;
        var first = (int)Math.Floor(position) % delayLine.Length;
        var next = (first + 1) % delayLine.Length;
        var fraction = position - Math.Floor(position);
        return delayLine[first] * (1d - fraction) + delayLine[next] * fraction;
    }

    private sealed class Biquad
    {
        private double b0 = 1d, b1, b2, a1, a2, z1, z2;

        public void Reset() => z1 = z2 = 0d;

        public double Process(double input)
        {
            var output = b0 * input + z1;
            z1 = b1 * input - a1 * output + z2;
            z2 = b2 * input - a2 * output;
            return output;
        }

        public void ConfigureLowPass(double frequency)
        {
            var omega = 2d * Math.PI * Math.Clamp(frequency, 20d, SampleRate * 0.49d) / SampleRate;
            var cosine = Math.Cos(omega);
            var alpha = Math.Sin(omega) / (2d * WebAudioBiquadQ.ToLinear((float)Math.Sqrt(0.5d)));
            var normalizer = 1d + alpha;
            b0 = (1d - cosine) / 2d / normalizer;
            b1 = (1d - cosine) / normalizer;
            b2 = b0;
            a1 = -2d * cosine / normalizer;
            a2 = (1d - alpha) / normalizer;
        }

        public void ConfigureLowShelf(double frequency, double gainDb)
        {
            if (gainDb == 0d)
            {
                b0 = 1d;
                b1 = b2 = a1 = a2 = 0d;
                return;
            }
            var amplitude = Math.Pow(10d, gainDb / 40d);
            var omega = 2d * Math.PI * frequency / SampleRate;
            var cosine = Math.Cos(omega);
            var alpha = Math.Sin(omega) / 2d * Math.Sqrt(2d);
            var beta = 2d * Math.Sqrt(amplitude) * alpha;
            var a0 = (amplitude + 1d) + (amplitude - 1d) * cosine + beta;
            b0 = amplitude * ((amplitude + 1d) - (amplitude - 1d) * cosine + beta) / a0;
            b1 = 2d * amplitude * ((amplitude - 1d) - (amplitude + 1d) * cosine) / a0;
            b2 = amplitude * ((amplitude + 1d) - (amplitude - 1d) * cosine - beta) / a0;
            a1 = -2d * ((amplitude - 1d) + (amplitude + 1d) * cosine) / a0;
            a2 = ((amplitude + 1d) + (amplitude - 1d) * cosine - beta) / a0;
        }

        public void ConfigureBandPass(double frequency, double q)
        {
            var omega = 2d * Math.PI * Math.Clamp(frequency, 20d, SampleRate * 0.49d) / SampleRate;
            var alpha = Math.Sin(omega) / (2d * q);
            var normalizer = 1d + alpha;
            b0 = alpha / normalizer;
            b1 = 0d;
            b2 = -b0;
            a1 = -2d * Math.Cos(omega) / normalizer;
            a2 = (1d - alpha) / normalizer;
        }
    }
}
