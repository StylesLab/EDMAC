namespace EDMAC.Effects;

public sealed class DelayEffect : IAudioEffect
{
    private readonly float[] leftBuffer;
    private readonly float[] rightBuffer;
    private readonly float feedback;
    private readonly float mix;
    private int position;

    public DelayEffect(int sampleRate, int delaySamples, float feedback, float mix)
    {
        leftBuffer = new float[Math.Max(1, delaySamples)];
        rightBuffer = new float[Math.Max(1, delaySamples)];
        this.feedback = Math.Clamp(feedback, 0, 0.98f);
        this.mix = Math.Clamp(mix, 0, 1);
    }

    public void Process(Span<float> left, Span<float> right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            float inputLeft = left[index];
            float inputRight = right[index];
            float delayedLeft = leftBuffer[position];
            float delayedRight = rightBuffer[position];

            left[index] = inputLeft + delayedLeft * mix;
            right[index] = inputRight + delayedRight * mix;

            leftBuffer[position] = inputLeft + delayedLeft * feedback;
            rightBuffer[position] = inputRight + delayedRight * feedback;

            position++;
            if (position == leftBuffer.Length)
            {
                position = 0;
            }
        }
    }
}
