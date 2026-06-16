namespace EDMAC.Effects;

public sealed class ReverbEffect : IAudioEffect
{
    private readonly Comb[] leftCombs;
    private readonly Comb[] rightCombs;
    private readonly float mix;

    public ReverbEffect(int sampleRate, float mix, float room, float damp)
    {
        this.mix = Math.Clamp(mix, 0, 1);
        float feedback = Math.Clamp(0.55f + room * 0.38f, 0.1f, 0.95f);
        float damping = Math.Clamp(damp, 0, 0.98f);

        leftCombs =
        [
            new Comb(Scale(sampleRate, 1116), feedback, damping),
            new Comb(Scale(sampleRate, 1188), feedback, damping),
            new Comb(Scale(sampleRate, 1277), feedback, damping),
            new Comb(Scale(sampleRate, 1356), feedback, damping)
        ];
        rightCombs =
        [
            new Comb(Scale(sampleRate, 1139), feedback, damping),
            new Comb(Scale(sampleRate, 1211), feedback, damping),
            new Comb(Scale(sampleRate, 1300), feedback, damping),
            new Comb(Scale(sampleRate, 1379), feedback, damping)
        ];
    }

    public void Process(Span<float> left, Span<float> right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            float inputLeft = left[index];
            float inputRight = right[index];
            float wetLeft = 0;
            float wetRight = 0;

            foreach (Comb comb in leftCombs)
            {
                wetLeft += comb.Process(inputLeft);
            }

            foreach (Comb comb in rightCombs)
            {
                wetRight += comb.Process(inputRight);
            }

            left[index] = inputLeft * (1 - mix) + wetLeft * 0.25f * mix;
            right[index] = inputRight * (1 - mix) + wetRight * 0.25f * mix;
        }
    }

    private static int Scale(int sampleRate, int samplesAt44100)
    {
        return Math.Max(1, (int)Math.Round(samplesAt44100 * sampleRate / 44100.0));
    }

    private sealed class Comb
    {
        private readonly float[] buffer;
        private readonly float feedback;
        private readonly float damp;
        private int position;
        private float filterStore;

        public Comb(int length, float feedback, float damp)
        {
            buffer = new float[length];
            this.feedback = feedback;
            this.damp = damp;
        }

        public float Process(float input)
        {
            float output = buffer[position];
            filterStore = output * (1 - damp) + filterStore * damp;
            buffer[position] = input + filterStore * feedback;
            position++;
            if (position == buffer.Length)
            {
                position = 0;
            }

            return output;
        }
    }
}
