namespace TanukiBCL.VoiceProbe;

// Port of TanukiBCL v3.2.7 renderer/lib/vad.ts and the Web Audio analyser
// processing used by AudioController.ts. The capture stream is 48 kHz mono.
internal sealed class TanukiVoiceActivityDetector
{
    private const int FftSize = 1024;
    private const int MinimumBin = 2; // round(85 / 24000 * 512)
    private const int MaximumBin = 5; // round(255 / 24000 * 512), exclusive
    private const double Smoothing = 0.2d;
    private const double ByteScale = 255d / 70d; // Web Audio defaults: -100..-30 dB
    private readonly double[] sampleRing = new double[FftSize];
    private readonly double[,] cosine = new double[MaximumBin - MinimumBin, FftSize];
    private readonly double[,] sine = new double[MaximumBin - MinimumBin, FftSize];
    private readonly double[] smoothedMagnitudes = new double[MaximumBin - MinimumBin];
    private int writeIndex;
    private int samplesSinceAnalysis;
    private int activityCounter;
    private volatile float minimumNoiseLevel = 0.15f;

    public TanukiVoiceActivityDetector()
    {
        for (var sample = 0; sample < FftSize; sample++)
        {
            var angle = 2d * Math.PI * sample / FftSize;
            var window = 0.42d - 0.5d * Math.Cos(angle) + 0.08d * Math.Cos(2d * angle);
            for (var bin = MinimumBin; bin < MaximumBin; bin++)
            {
                cosine[bin - MinimumBin, sample] = window * Math.Cos(bin * angle);
                sine[bin - MinimumBin, sample] = window * Math.Sin(bin * angle);
            }
        }
    }

    public bool IsTalking { get; private set; }

    public double LastBandLevel { get; private set; }

    public void SetMinimumNoiseLevel(double level) =>
        minimumNoiseLevel = (float)Math.Clamp(level, 0d, 1d);

    public void ResetActivity()
    {
        activityCounter = 0;
        IsTalking = false;
    }

    public bool ProcessPcm16(ReadOnlySpan<byte> pcm)
    {
        for (var offset = 0; offset + sizeof(short) <= pcm.Length; offset += sizeof(short))
        {
            sampleRing[writeIndex] = BitConverter.ToInt16(pcm.Slice(offset, sizeof(short))) / 32768d;
            writeIndex = (writeIndex + 1) % FftSize;
            if (++samplesSinceAnalysis == FftSize)
            {
                samplesSinceAnalysis = 0;
                AnalyseLatestBlock();
            }
        }

        return IsTalking;
    }

    private void AnalyseLatestBlock()
    {
        double bandSum = 0d;
        for (var bin = 0; bin < MaximumBin - MinimumBin; bin++)
        {
            double real = 0d;
            double imaginary = 0d;
            for (var sample = 0; sample < FftSize; sample++)
            {
                var value = sampleRing[(writeIndex + sample) % FftSize];
                real += value * cosine[bin, sample];
                imaginary -= value * sine[bin, sample];
            }

            var magnitude = Math.Sqrt(real * real + imaginary * imaginary) / FftSize;
            smoothedMagnitudes[bin] = Smoothing * smoothedMagnitudes[bin] + (1d - Smoothing) * magnitude;
            var decibels = smoothedMagnitudes[bin] > 0d
                ? 20d * Math.Log10(smoothedMagnitudes[bin])
                : double.NegativeInfinity;
            var byteLevel = Math.Clamp(Math.Floor((decibels + 100d) * ByteScale), 0d, 255d);
            bandSum += byteLevel / 255d;
        }

        LastBandLevel = bandSum / (MaximumBin - MinimumBin);
        // AudioController passes noiseCaptureDuration=0 and immediately calls
        // init(), so the empty environment sample falls back to minNoiseLevel.
        var configuredMinimum = minimumNoiseLevel;
        var baseline = Math.Clamp((configuredMinimum > 0f ? configuredMinimum : 0.1d) * 1.2d,
            configuredMinimum, 1d);
        activityCounter = Math.Clamp(activityCounter + (LastBandLevel >= baseline ? 1 : -1), 0, 30);
        IsTalking = activityCounter > 5;
    }
}
