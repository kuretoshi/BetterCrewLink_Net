using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// TanukiBCL v3.2.7 radio echo: 90 ms delay, 0.92 dry, 0.2 wet, 0.12 feedback.
internal sealed class RadioEchoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly int delaySamples;
    private readonly float dryGain;
    private readonly float wetGain;
    private readonly float feedbackGain;
    private readonly bool scaleFeedbackWithStrength;
    private float[]? delayLine;
    private int delayPosition;
    private bool wasEnabled;
    private float appliedStrength;
    private int resetRequested;
    public bool SmoothTransitions { get; init; }
    public volatile bool Enabled;
    public volatile float Strength = 1f;

    public RadioEchoSampleProvider(ISampleProvider source, double delaySeconds = 0.09d,
        float dryGain = 0.92f, float wetGain = 0.2f, float feedbackGain = 0.12f,
        bool scaleFeedbackWithStrength = false)
    {
        this.source = source;
        this.dryGain = dryGain;
        this.wetGain = wetGain;
        this.feedbackGain = feedbackGain;
        this.scaleFeedbackWithStrength = scaleFeedbackWithStrength;
        delaySamples = checked((int)Math.Round(source.WaveFormat.SampleRate * delaySeconds) *
            source.WaveFormat.Channels);
    }

    public WaveFormat WaveFormat => source.WaveFormat;
    internal int DelayCapacitySamples => delayLine?.Length ?? 0;
    internal void Reset() => Volatile.Write(ref resetRequested, 1);
    internal bool IsProcessing => Enabled || wasEnabled;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (Interlocked.Exchange(ref resetRequested, 0) != 0)
        {
            delayLine = null;
            delayPosition = 0;
            appliedStrength = 0;
            wasEnabled = false;
        }
        var requestedEnabled = Enabled;
        var enabled = requestedEnabled || SmoothTransitions && wasEnabled && appliedStrength > 0;
        if (enabled != wasEnabled)
        {
            delayLine = enabled ? new float[delaySamples] : null;
            delayPosition = 0;
            wasEnabled = enabled;
        }
        if (!enabled) return read;

        var line = delayLine!;
        var target = requestedEnabled ? Math.Clamp(Strength, 0f, 1f) : 0;
        var step = 1f / (Math.Max(1, WaveFormat.SampleRate / 100) * WaveFormat.Channels);
        for (var index = offset; index < offset + read; index++)
        {
            appliedStrength = SmoothTransitions
                ? Math.Clamp(target, appliedStrength - step, appliedStrength + step) : target;
            var strength = appliedStrength;
            var input = buffer[index];
            var delayed = line[delayPosition];
            line[delayPosition] = input + feedbackGain * delayed * (scaleFeedbackWithStrength ? strength : 1f);
            buffer[index] = (1f - (1f - dryGain) * strength) * input + wetGain * strength * delayed;
            if (++delayPosition == line.Length) delayPosition = 0;
        }
        if (!requestedEnabled && appliedStrength == 0)
        {
            delayLine = null;
            delayPosition = 0;
            wasEnabled = false;
        }
        return read;
    }
}
