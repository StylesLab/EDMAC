namespace EDMAC.Effects;

public sealed class ModulatedDelayEffect : IAudioEffect
{
    private readonly float[] leftBuffer;
    private readonly float[] rightBuffer;
    private readonly float baseDelaySamples;
    private readonly float depthSamples;
    private readonly float rateHz;
    private readonly float feedback;
    private readonly float mix;
    private readonly int sampleRate;
    private int writePosition;
    private double phase;

    public ModulatedDelayEffect(
        int sampleRate,
        float delayMs,
        float depthMs,
        float rateHz,
        float feedback,
        float mix)
    {
        this.sampleRate = sampleRate;
        baseDelaySamples = Math.Max(1, delayMs * sampleRate / 1000.0f);
        depthSamples = Math.Max(0, depthMs * sampleRate / 1000.0f);
        this.rateHz = Math.Max(0.01f, rateHz);
        this.feedback = Math.Clamp(feedback, -0.95f, 0.95f);
        this.mix = Math.Clamp(mix, 0, 1);
        int bufferLength = Math.Max(2, (int)Math.Ceiling(baseDelaySamples + depthSamples + 4));
        leftBuffer = new float[bufferLength];
        rightBuffer = new float[bufferLength];
    }

    public void Process(Span<float> left, Span<float> right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            float modulation = (float)((Math.Sin(phase) + 1) * 0.5);
            float delay = baseDelaySamples + depthSamples * modulation;
            float delayedLeft = ReadInterpolated(leftBuffer, delay);
            float delayedRight = ReadInterpolated(rightBuffer, delay * 0.97f);
            float inputLeft = left[index];
            float inputRight = right[index];

            left[index] = inputLeft * (1 - mix) + delayedLeft * mix;
            right[index] = inputRight * (1 - mix) + delayedRight * mix;

            leftBuffer[writePosition] = inputLeft + delayedLeft * feedback;
            rightBuffer[writePosition] = inputRight + delayedRight * feedback;

            writePosition++;
            if (writePosition == leftBuffer.Length)
            {
                writePosition = 0;
            }

            phase += 2 * Math.PI * rateHz / sampleRate;
            if (phase >= 2 * Math.PI)
            {
                phase -= 2 * Math.PI;
            }
        }
    }

    private float ReadInterpolated(float[] buffer, float delay)
    {
        float readPosition = writePosition - delay;
        while (readPosition < 0)
        {
            readPosition += buffer.Length;
        }

        int index0 = (int)readPosition;
        int index1 = (index0 + 1) % buffer.Length;
        float fraction = readPosition - index0;
        return buffer[index0] + (buffer[index1] - buffer[index0]) * fraction;
    }
}
