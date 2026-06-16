namespace EDMAC.Effects;

public enum FilterMode
{
    LowPass,
    HighPass,
    BandPass
}

public sealed class BiquadFilterEffect : IAudioEffect
{
    private readonly FilterMode mode;
    private readonly int sampleRate;
    private readonly float q;
    private readonly float startCutoff;
    private readonly float endCutoff;
    private readonly long sweepSamples;
    private BiquadState leftState;
    private BiquadState rightState;
    private Coefficients coefficients;
    private long processedSamples;
    private float lastCutoff = -1;

    public BiquadFilterEffect(
        FilterMode mode,
        int sampleRate,
        float cutoff,
        float q,
        float? from,
        float? to,
        float durationBeats,
        double bpm)
    {
        this.mode = mode;
        this.sampleRate = sampleRate;
        this.q = Math.Clamp(q, 0.1f, 20);
        startCutoff = from ?? cutoff;
        endCutoff = to ?? startCutoff;
        sweepSamples = durationBeats > 0
            ? Math.Max(1, (long)Math.Round(durationBeats * 60.0 / bpm * sampleRate))
            : 0;
        UpdateCoefficients(startCutoff);
    }

    public void Process(Span<float> left, Span<float> right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            if (sweepSamples > 0)
            {
                float progress = (processedSamples % sweepSamples) / (float)sweepSamples;
                UpdateCoefficients(startCutoff + (endCutoff - startCutoff) * progress);
            }

            left[index] = ProcessSample(left[index], ref leftState);
            right[index] = ProcessSample(right[index], ref rightState);
            processedSamples++;
        }
    }

    private float ProcessSample(float input, ref BiquadState state)
    {
        float output =
            coefficients.B0 * input +
            coefficients.B1 * state.X1 +
            coefficients.B2 * state.X2 -
            coefficients.A1 * state.Y1 -
            coefficients.A2 * state.Y2;

        state.X2 = state.X1;
        state.X1 = input;
        state.Y2 = state.Y1;
        state.Y1 = output;
        return output;
    }

    private void UpdateCoefficients(float cutoff)
    {
        cutoff = Math.Clamp(cutoff, 20, sampleRate * 0.45f);
        if (Math.Abs(cutoff - lastCutoff) < 0.01f)
        {
            return;
        }

        lastCutoff = cutoff;
        double omega = 2 * Math.PI * cutoff / sampleRate;
        double sin = Math.Sin(omega);
        double cos = Math.Cos(omega);
        double alpha = sin / (2 * q);
        double b0;
        double b1;
        double b2;
        double a0 = 1 + alpha;
        double a1 = -2 * cos;
        double a2 = 1 - alpha;

        switch (mode)
        {
            case FilterMode.LowPass:
                b0 = (1 - cos) / 2;
                b1 = 1 - cos;
                b2 = (1 - cos) / 2;
                break;
            case FilterMode.HighPass:
                b0 = (1 + cos) / 2;
                b1 = -(1 + cos);
                b2 = (1 + cos) / 2;
                break;
            default:
                b0 = sin / 2;
                b1 = 0;
                b2 = -sin / 2;
                break;
        }

        coefficients = new Coefficients(
            (float)(b0 / a0),
            (float)(b1 / a0),
            (float)(b2 / a0),
            (float)(a1 / a0),
            (float)(a2 / a0));
    }

    private readonly record struct Coefficients(
        float B0,
        float B1,
        float B2,
        float A1,
        float A2);

    private struct BiquadState
    {
        public float X1;
        public float X2;
        public float Y1;
        public float Y2;
    }
}
