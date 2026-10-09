namespace TanukiBCL.VoiceProbe;

// Port of TanukiBCL 3.2.21 sourceFilterProcessor.ts: cepstral envelope,
// independently transposed excitation, warped formants and overlap-add.
internal sealed class SourceFilterDsp
{
    private const int Size = 2048;
    private const int Hop = 512;
    private const int Half = Size / 2;
    private readonly int sampleRate;
    private readonly double[] input = new double[Size];
    private readonly double[] output = new double[Size];
    private readonly double[] window = new double[Size];
    private readonly double[] real = new double[Size];
    private readonly double[] imaginary = new double[Size];
    private readonly double[] cepReal = new double[Size];
    private readonly double[] cepImaginary = new double[Size];
    private readonly double[] magnitude = new double[Half + 1];
    private readonly double[] logMagnitude = new double[Half + 1];
    private readonly double[] envelope = new double[Half + 1];
    private readonly double[] lastPhase = new double[Half + 1];
    private readonly double[] phase = new double[Half + 1];
    private readonly double[] weights = new double[Half + 1];
    private readonly double[] frequencies = new double[Half + 1];
    private readonly double[] excitation = new double[Half + 1];
    private int cursor;
    private int frames;
    private double pitch = 1d;
    private double formant = 1d;
    private double squash;
    private double requestedPitch = 1d;
    private double requestedFormant = 1d;
    private double requestedSquash;

    public SourceFilterDsp(int sampleRate)
    {
        this.sampleRate = sampleRate;
        for (var index = 0; index < Size; index++)
            window[index] = 0.5d - 0.5d * Math.Cos(2d * Math.PI * index / Size);
    }

    public void SetParameters(double nextPitch, double nextFormant, double nextSquash)
    {
        requestedPitch = Math.Clamp(nextPitch, 0.4d, 2d);
        requestedFormant = Math.Clamp(nextFormant, 0.55d, 1.7d);
        requestedSquash = Math.Clamp(nextSquash, 0d, 1d);
    }

    public void Process(float[] samples, int offset, int count)
    {
        for (var index = offset; index < offset + count; index++)
        {
            var value = samples[index];
            input[Size - Hop + cursor] = float.IsFinite(value) ? value : 0d;
            var rendered = output[cursor];
            samples[index] = double.IsFinite(rendered) ? (float)Math.Clamp(rendered, -1d, 1d) : 0f;
            if (++cursor != Hop) continue;
            Array.Copy(output, Hop, output, 0, Size - Hop);
            Array.Clear(output, Size - Hop, Hop);
            SmoothParameters();
            Frame();
            cursor = 0;
        }
    }

    private void SmoothParameters()
    {
        if (frames == 0)
        {
            pitch = requestedPitch;
            formant = requestedFormant;
            squash = requestedSquash;
            return;
        }
        var smoothing = 1d - Math.Exp(-Hop / (sampleRate * 0.04d));
        pitch += smoothing * (requestedPitch - pitch);
        formant += smoothing * (requestedFormant - formant);
        squash += smoothing * (requestedSquash - squash);
    }

    private void Frame()
    {
        for (var index = 0; index < Size; index++)
        {
            real[index] = input[index] * window[index];
            imaginary[index] = 0d;
        }
        Fft(real, imaginary);
        EstimateEnvelope();
        MapExcitation();
        Synthesize();
        Array.Copy(input, Hop, input, 0, Size - Hop);
        Array.Clear(input, Size - Hop, Hop);
        frames++;
    }

    private void EstimateEnvelope()
    {
        for (var bin = 0; bin <= Half; bin++)
        {
            magnitude[bin] = Math.Sqrt(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]);
            logMagnitude[bin] = Math.Log(Math.Max(1e-6d, magnitude[bin]));
            cepReal[bin] = logMagnitude[bin];
            cepImaginary[bin] = 0d;
            if (bin is > 0 and < Half)
            {
                cepReal[Size - bin] = cepReal[bin];
                cepImaginary[Size - bin] = 0d;
            }
        }
        var limit = Math.Min(Half, (int)Math.Round(sampleRate * 0.003d));
        for (var iteration = 0; iteration < 4; iteration++)
        {
            if (iteration > 0) RaiseCepstralEnvelope();
            Fft(cepReal, cepImaginary, inverse: true);
            LifterCepstrum(limit);
            Fft(cepReal, cepImaginary);
        }
        for (var bin = 0; bin <= Half; bin++)
            envelope[bin] = Math.Exp(Math.Clamp(cepReal[bin], -14d, 14d));
    }

    private void RaiseCepstralEnvelope()
    {
        for (var bin = 0; bin <= Half; bin++)
        {
            cepReal[bin] = Math.Max(logMagnitude[bin], cepReal[bin]);
            cepImaginary[bin] = 0d;
            if (bin is > 0 and < Half)
            {
                cepReal[Size - bin] = cepReal[bin];
                cepImaginary[Size - bin] = 0d;
            }
        }
    }

    private void LifterCepstrum(int limit)
    {
        for (var index = 1; index < Size; index++)
        {
            var distance = Math.Min(index, Size - index);
            var gain = distance <= limit * 0.75d ? 1d
                : distance < limit
                    ? 0.5d + 0.5d * Math.Cos(Math.PI * (distance / (double)limit - 0.75d) / 0.25d)
                    : 0d;
            cepReal[index] *= gain;
            cepImaginary[index] = 0d;
        }
    }

    private void MapExcitation()
    {
        Array.Clear(weights);
        Array.Clear(frequencies);
        Array.Clear(excitation);
        var step = 2d * Math.PI * Hop / Size;
        for (var bin = 0; bin <= Half; bin++)
        {
            var currentPhase = Math.Atan2(imaginary[bin], real[bin]);
            var delta = currentPhase - lastPhase[bin] - bin * step;
            delta -= 2d * Math.PI * Math.Round(delta / (2d * Math.PI));
            lastPhase[bin] = currentPhase;
            if (Math.Abs(pitch - 1d) < 1e-4d) continue;
            var target = (int)Math.Round(bin * pitch);
            if (target > Half) continue;
            var weight = magnitude[bin];
            excitation[target] += weight / Math.Max(1e-6d, envelope[bin]);
            weights[target] += weight;
            frequencies[target] += (bin + delta / step) * pitch * weight;
        }
    }

    private void Synthesize()
    {
        var before = 0d;
        var after = 0d;
        var cutoff = 12000d * Math.Pow(900d / 12000d, squash);
        var step = 2d * Math.PI * Hop / Size;
        for (var bin = 0; bin <= Half; bin++)
        {
            var filter = Interpolate(envelope, bin / formant);
            var mappedMagnitude = Math.Abs(pitch - 1d) < 1e-4d
                ? SamePitchMagnitude(bin, filter)
                : ShiftedMagnitude(bin, filter, step);
            before += magnitude[bin] * magnitude[bin];
            after += mappedMagnitude * mappedMagnitude;
            var frequency = bin * sampleRate / (double)Size;
            var muffle = squash > 0d ? 1d / Math.Sqrt(1d + Math.Pow(frequency / cutoff, 4d)) : 1d;
            var amplitude = mappedMagnitude * muffle;
            real[bin] = amplitude * Math.Cos(phase[bin]);
            imaginary[bin] = amplitude * Math.Sin(phase[bin]);
            if (bin is > 0 and < Half)
            {
                real[Size - bin] = real[bin];
                imaginary[Size - bin] = -imaginary[bin];
            }
        }
        imaginary[0] = 0d;
        imaginary[Half] = 0d;
        var gain = Math.Min(1d, Math.Sqrt(before / Math.Max(1e-12d, after))) * (1d - 0.3d * squash);
        Fft(real, imaginary, inverse: true);
        for (var index = 0; index < Size; index++)
            output[index] += real[index] * window[index] * gain / ((Size / Hop) * 0.375d);
    }

    private double SamePitchMagnitude(int bin, double filter)
    {
        phase[bin] = lastPhase[bin];
        return magnitude[bin] * Math.Min(32d, filter / Math.Max(1e-6d, envelope[bin]));
    }

    private double ShiftedMagnitude(int bin, double filter, double step)
    {
        var frequency = weights[bin] > 1e-9d ? frequencies[bin] / weights[bin] : bin;
        phase[bin] = (phase[bin] + frequency * step) % (2d * Math.PI);
        var candidate = excitation[bin] * filter;
        var reference = Interpolate(magnitude, bin / pitch);
        return Math.Min(candidate, reference * 32d);
    }

    private static double Interpolate(double[] data, double position)
    {
        if (position < 0d || position >= data.Length - 1)
            return data[Math.Clamp((int)Math.Floor(position), 0, data.Length - 1)];
        var lower = (int)Math.Floor(position);
        var fraction = position - lower;
        return data[lower] * (1d - fraction) + data[lower + 1] * fraction;
    }

    private static void Fft(double[] real, double[] imaginary, bool inverse = false)
    {
        var size = real.Length;
        var reverse = 0;
        for (var index = 1; index < size; index++)
        {
            var bit = size >> 1;
            while ((reverse & bit) != 0)
            {
                reverse ^= bit;
                bit >>= 1;
            }
            reverse ^= bit;
            if (index >= reverse) continue;
            (real[index], real[reverse]) = (real[reverse], real[index]);
            (imaginary[index], imaginary[reverse]) = (imaginary[reverse], imaginary[index]);
        }
        for (var width = 2; width <= size; width *= 2)
            FftStage(real, imaginary, width, inverse);
        if (!inverse) return;
        for (var index = 0; index < size; index++)
        {
            real[index] /= size;
            imaginary[index] /= size;
        }
    }

    private static void FftStage(double[] real, double[] imaginary, int width, bool inverse)
    {
        var angle = (inverse ? 2d : -2d) * Math.PI / width;
        var stepReal = Math.Cos(angle);
        var stepImaginary = Math.Sin(angle);
        for (var start = 0; start < real.Length; start += width)
        {
            var twiddleReal = 1d;
            var twiddleImaginary = 0d;
            for (var index = 0; index < width / 2; index++)
            {
                var a = start + index;
                var b = a + width / 2;
                var transformedReal = real[b] * twiddleReal - imaginary[b] * twiddleImaginary;
                var transformedImaginary = real[b] * twiddleImaginary + imaginary[b] * twiddleReal;
                real[b] = real[a] - transformedReal;
                imaginary[b] = imaginary[a] - transformedImaginary;
                real[a] += transformedReal;
                imaginary[a] += transformedImaginary;
                var next = twiddleReal * stepReal - twiddleImaginary * stepImaginary;
                twiddleImaginary = twiddleReal * stepImaginary + twiddleImaginary * stepReal;
                twiddleReal = next;
            }
        }
    }
}
