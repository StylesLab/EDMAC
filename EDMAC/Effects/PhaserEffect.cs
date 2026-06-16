namespace EDMAC.Effects;

public sealed class PhaserEffect : IAudioEffect
{
    private readonly AllPass[] leftStages;
    private readonly AllPass[] rightStages;
    private readonly int sampleRate;
    private readonly float rateHz;
    private readonly float depth;
    private readonly float feedback;
    private readonly float mix;
    private double phase;
    private float leftFeedback;
    private float rightFeedback;

    public PhaserEffect(int sampleRate, float rateHz, float depth, float feedback, float mix)
    {
        this.sampleRate = sampleRate;
        this.rateHz = Math.Max(0.01f, rateHz);
        this.depth = Math.Clamp(depth, 0, 1);
        this.feedback = Math.Clamp(feedback, -0.95f, 0.95f);
        this.mix = Math.Clamp(mix, 0, 1);
        leftStages = [new(), new(), new(), new()];
        rightStages = [new(), new(), new(), new()];
    }

    public void Process(Span<float> left, Span<float> right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            float coefficient = 0.15f + (float)((Math.Sin(phase) + 1) * 0.5) * 0.75f * depth;
            float inputLeft = left[index];
            float inputRight = right[index];
            float phasedLeft = inputLeft + leftFeedback * feedback;
            float phasedRight = inputRight + rightFeedback * feedback;

            foreach (AllPass stage in leftStages)
            {
                phasedLeft = stage.Process(phasedLeft, coefficient);
            }

            foreach (AllPass stage in rightStages)
            {
                phasedRight = stage.Process(phasedRight, coefficient * 0.97f);
            }

            leftFeedback = phasedLeft;
            rightFeedback = phasedRight;
            left[index] = inputLeft * (1 - mix) + phasedLeft * mix;
            right[index] = inputRight * (1 - mix) + phasedRight * mix;

            phase += 2 * Math.PI * rateHz / sampleRate;
            if (phase >= 2 * Math.PI)
            {
                phase -= 2 * Math.PI;
            }
        }
    }

    private sealed class AllPass
    {
        private float previousInput;
        private float previousOutput;

        public float Process(float input, float coefficient)
        {
            float output = -coefficient * input + previousInput + coefficient * previousOutput;
            previousInput = input;
            previousOutput = output;
            return output;
        }
    }
}
