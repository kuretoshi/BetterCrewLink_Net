using System.Numerics;
using NAudio.Wave;

namespace TanukiBCL.VoiceProbe;

// The released client feeds its stereo ghost voice through a normalized Web Audio
// ConvolverNode. Use the same impulse response and equal-power normalization here.
// Uniform partitioned convolution keeps the three-second response off the audio
// callback's O(response length) path. Each peer owns only its input/history state.
internal sealed class GhostReverbSampleProvider : ISampleProvider
{
    private const int BlockFrames = 1024;
    private const int FftLength = BlockFrames * 2;
    private const int HistoryRetentionFrames = 48_000 * 10;
    private static readonly Lazy<ImpulseSpectrum> ReleasedImpulse = new(LoadReleasedImpulse);

    private readonly ISampleProvider source;
    private ImpulseSpectrum? impulse;
    private Complex[][] leftHistory = [];
    private Complex[][] rightHistory = [];
    private Complex[] leftFft = [];
    private Complex[] rightFft = [];
    private Complex[] leftSum = [];
    private Complex[] rightSum = [];
    private float[] input = [];
    private float[] output = [];
    private float[] leftOverlap = [];
    private float[] rightOverlap = [];
    private int framePosition;
    private int historyPosition;
    private int disabledFrames;
    private bool wasEnabled;
    private volatile bool enabled;

    public bool Enabled
    {
        get => enabled;
        set
        {
            // Prepare the shared response on the game-state thread, before
            // publishing the enabled flag to the audio callback.
            if (value) impulse ??= ReleasedImpulse.Value;
            enabled = value;
        }
    }

    public GhostReverbSampleProvider(ISampleProvider source)
        : this(source, (ImpulseSpectrum?)null)
    {
    }

    internal GhostReverbSampleProvider(ISampleProvider source, float[] stereoImpulse)
        : this(source, CreateImpulse(stereoImpulse, source.WaveFormat.SampleRate, normalize: false))
    {
    }

    private GhostReverbSampleProvider(ISampleProvider source, ImpulseSpectrum? impulse)
    {
        if (source.WaveFormat.SampleRate != 48_000 || source.WaveFormat.Channels != 2)
            throw new ArgumentException("Ghost reverb requires 48 kHz stereo input.", nameof(source));

        this.source = source;
        this.impulse = impulse;
    }

    public WaveFormat WaveFormat => source.WaveFormat;
    internal long PreparedImpulseBytes => impulse is null ? 0 :
        (long)(impulse.Left.Length + impulse.Right.Length) * FftLength * sizeof(double) * 2;

    internal long RetainedHistoryBytes =>
        (long)(leftHistory.Length + rightHistory.Length) * FftLength * sizeof(double) * 2;
    internal long RetainedWorkingBytes =>
        (long)(leftFft.Length + rightFft.Length + leftSum.Length + rightSum.Length) * sizeof(double) * 2 +
        (long)(input.Length + output.Length + leftOverlap.Length + rightOverlap.Length) * sizeof(float);

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if ((read & 1) != 0)
            throw new InvalidOperationException("Stereo playback must contain complete frames.");

        var enabled = Enabled;
        if (enabled != wasEnabled)
        {
            if (enabled)
            {
                if (leftHistory.Length == 0)
                {
                    var response = impulse ?? throw new InvalidOperationException("Ghost impulse was not prepared.");
                    leftHistory = AllocateHistory(response.Left.Length);
                    rightHistory = AllocateHistory(response.Right.Length);
                }
                if (leftFft.Length == 0) AllocateWorkingBuffers();
            }
            Reset(clearHistory: enabled);
            disabledFrames = 0;
            wasEnabled = enabled;
        }
        if (!enabled)
        {
            // Retain briefly across distance changes to avoid repeatedly allocating
            // large convolution buffers when players cross the audible boundary.
            disabledFrames = Math.Min(HistoryRetentionFrames, disabledFrames + read / 2);
            if (disabledFrames == HistoryRetentionFrames)
            {
                leftHistory = [];
                rightHistory = [];
                leftFft = rightFft = leftSum = rightSum = [];
                input = output = leftOverlap = rightOverlap = [];
            }
            return read;
        }

        for (var index = offset; index < offset + read; index += 2)
        {
            var position = framePosition * 2;
            var left = buffer[index];
            var right = buffer[index + 1];
            buffer[index] = output[position];
            buffer[index + 1] = output[position + 1];
            input[position] = left;
            input[position + 1] = right;
            if (++framePosition == BlockFrames)
            {
                ProcessBlock();
                framePosition = 0;
            }
        }
        return read;
    }

    private void ProcessBlock()
    {
        var response = impulse!;
        Array.Clear(leftFft);
        Array.Clear(rightFft);
        for (var frame = 0; frame < BlockFrames; frame++)
        {
            leftFft[frame] = new Complex(input[frame * 2], 0);
            rightFft[frame] = new Complex(input[frame * 2 + 1], 0);
        }
        Transform(leftFft, inverse: false);
        Transform(rightFft, inverse: false);
        leftFft.CopyTo(leftHistory[historyPosition], 0);
        rightFft.CopyTo(rightHistory[historyPosition], 0);

        Array.Clear(leftSum);
        Array.Clear(rightSum);
        for (var partition = 0; partition < response.Left.Length; partition++)
        {
            var index = historyPosition - partition;
            if (index < 0) index += response.Left.Length;
            var leftInput = leftHistory[index];
            var rightInput = rightHistory[index];
            var leftResponse = response.Left[partition];
            var rightResponse = response.Right[partition];
            for (var bin = 0; bin < FftLength; bin++)
            {
                leftSum[bin] += leftInput[bin] * leftResponse[bin];
                rightSum[bin] += rightInput[bin] * rightResponse[bin];
            }
        }
        Transform(leftSum, inverse: true);
        Transform(rightSum, inverse: true);
        for (var frame = 0; frame < BlockFrames; frame++)
        {
            output[frame * 2] = (float)leftSum[frame].Real + leftOverlap[frame];
            output[frame * 2 + 1] = (float)rightSum[frame].Real + rightOverlap[frame];
            leftOverlap[frame] = (float)leftSum[frame + BlockFrames].Real;
            rightOverlap[frame] = (float)rightSum[frame + BlockFrames].Real;
        }
        if (++historyPosition == response.Left.Length) historyPosition = 0;
    }

    private void Reset(bool clearHistory)
    {
        if (clearHistory)
        {
            foreach (var block in leftHistory) Array.Clear(block);
            foreach (var block in rightHistory) Array.Clear(block);
        }
        Array.Clear(input);
        Array.Clear(output);
        Array.Clear(leftOverlap);
        Array.Clear(rightOverlap);
        framePosition = 0;
        historyPosition = 0;
    }

    private void AllocateWorkingBuffers()
    {
        leftFft = new Complex[FftLength];
        rightFft = new Complex[FftLength];
        leftSum = new Complex[FftLength];
        rightSum = new Complex[FftLength];
        input = new float[BlockFrames * 2];
        output = new float[BlockFrames * 2];
        leftOverlap = new float[BlockFrames];
        rightOverlap = new float[BlockFrames];
    }

    private static Complex[][] AllocateHistory(int partitions)
    {
        var history = new Complex[partitions][];
        for (var index = 0; index < partitions; index++)
            history[index] = new Complex[FftLength];
        return history;
    }

    private static ImpulseSpectrum LoadReleasedImpulse()
    {
        using var stream = typeof(GhostReverbSampleProvider).Assembly.GetManifestResourceStream(
            "TanukiBCL.VoiceProbe.Assets.Audio.reverb.48k.wav")
            ?? throw new InvalidOperationException("Released ghost reverb impulse is missing.");
        using var wave = new WaveFileReader(stream);
        if (wave.WaveFormat.SampleRate != 48_000 || wave.WaveFormat.Channels != 2)
            throw new InvalidDataException("Unexpected ghost reverb impulse format.");

        var samples = new float[checked((int)(wave.Length / wave.WaveFormat.BlockAlign) * 2)];
        var provider = wave.ToSampleProvider();
        var read = 0;
        while (read < samples.Length)
        {
            var count = provider.Read(samples, read, samples.Length - read);
            if (count == 0) break;
            read += count;
        }
        if (read < 2 || (read & 1) != 0)
            throw new InvalidDataException("Released ghost reverb impulse is empty or incomplete.");
        if (read != samples.Length) Array.Resize(ref samples, read);
        return CreateImpulse(samples, wave.WaveFormat.SampleRate, normalize: true);
    }

    private static ImpulseSpectrum CreateImpulse(float[] samples, int sampleRate, bool normalize)
    {
        if (samples.Length < 2 || (samples.Length & 1) != 0)
            throw new ArgumentException("A stereo impulse response is required.", nameof(samples));

        var scale = 1d;
        if (normalize)
        {
            var sumSquares = 0d;
            foreach (var sample in samples) sumSquares += (double)sample * sample;
            var power = Math.Max(Math.Sqrt(sumSquares / samples.Length), 0.000125d);
            // Web Audio 1.0 ConvolverNode's default normalize=true formula.
            scale = 0.00125d * 44_100d / sampleRate / power;
        }

        var frames = samples.Length / 2;
        var partitions = (frames + BlockFrames - 1) / BlockFrames;
        var left = AllocateHistory(partitions);
        var right = AllocateHistory(partitions);
        for (var frame = 0; frame < frames; frame++)
        {
            var partition = frame / BlockFrames;
            var position = frame % BlockFrames;
            left[partition][position] = new Complex(samples[frame * 2] * scale, 0);
            right[partition][position] = new Complex(samples[frame * 2 + 1] * scale, 0);
        }
        foreach (var block in left) Transform(block, inverse: false);
        foreach (var block in right) Transform(block, inverse: false);
        return new ImpulseSpectrum(left, right);
    }

    private static void Transform(Complex[] values, bool inverse)
    {
        for (int index = 1, reverse = 0; index < FftLength; index++)
        {
            var bit = FftLength >> 1;
            for (; (reverse & bit) != 0; bit >>= 1) reverse ^= bit;
            reverse ^= bit;
            if (index < reverse) (values[index], values[reverse]) = (values[reverse], values[index]);
        }

        for (var length = 2; length <= FftLength; length <<= 1)
        {
            var angle = (inverse ? 2d : -2d) * Math.PI / length;
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < FftLength; start += length)
            {
                var rotation = Complex.One;
                for (var index = 0; index < length / 2; index++)
                {
                    var even = values[start + index];
                    var odd = rotation * values[start + index + length / 2];
                    values[start + index] = even + odd;
                    values[start + index + length / 2] = even - odd;
                    rotation *= step;
                }
            }
        }
        if (inverse)
            for (var index = 0; index < FftLength; index++) values[index] /= FftLength;
    }

    private sealed record ImpulseSpectrum(Complex[][] Left, Complex[][] Right);
}
