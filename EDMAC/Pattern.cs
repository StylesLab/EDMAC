namespace EDMAC;

public sealed class Pattern
{
    public const char RestSymbol = '.';
    public const char TieSymbol = '>';
    public const char PlayToCompletionSymbol = '+';

    private readonly bool[] playToCompletionSteps;

    public Pattern(string source, int beats, int sampleRate, double bpm)
    {
        var steps = new List<char>();
        var playToCompletion = new List<bool>();

        foreach (char character in source.Where(character => character != '|'))
        {
            if (character == PlayToCompletionSymbol)
            {
                if (steps.Count == 0 ||
                    steps[^1] == RestSymbol ||
                    steps[^1] == TieSymbol ||
                    playToCompletion[^1])
                {
                    throw new InvalidDataException(
                        "'+' must immediately follow a pattern mapping symbol.");
                }

                playToCompletion[^1] = true;
                continue;
            }

            steps.Add(character);
            playToCompletion.Add(false);
        }

        Steps = new string(steps.ToArray());
        playToCompletionSteps = playToCompletion.ToArray();
        if (Steps.Length == 0)
        {
            throw new InvalidDataException("A pattern must contain at least one step.");
        }

        if (beats <= 0)
        {
            throw new InvalidDataException("Track beats must be greater than zero.");
        }

        Beats = beats;
        SamplesPerStep = sampleRate * 240.0 / (bpm * beats);

        if (SamplesPerStep <= 0 ||
            double.IsNaN(SamplesPerStep) ||
            double.IsInfinity(SamplesPerStep))
        {
            throw new InvalidDataException("The BPM and beats values produce an invalid step length.");
        }
    }

    public string Steps { get; }

    public int Beats { get; }

    public int Length => Steps.Length;

    public double SamplesPerStep { get; }

    public char GetSymbol(long absoluteStep)
    {
        return Steps[(int)(absoluteStep % Length)];
    }

    public bool IsTieStep(long absoluteStep)
    {
        return GetSymbol(absoluteStep) == TieSymbol;
    }

    public bool ShouldTrigger(long absoluteStep)
    {
        int step = (int)(absoluteStep % Length);
        return !playToCompletionSteps[step] || absoluteStep < Length;
    }

    public bool PlaysToCompletion(long absoluteStep)
    {
        return playToCompletionSteps[(int)(absoluteStep % Length)];
    }

    public bool HasRepeatingTriggers => Steps
        .Select((symbol, index) => (symbol, index))
        .Any(step => step.symbol != RestSymbol &&
                     step.symbol != TieSymbol &&
                     !playToCompletionSteps[step.index]);

    public bool HasPlayToCompletionSteps => playToCompletionSteps.Any(value => value);

    public long GetSamplePosition(long absoluteStep)
    {
        return checked((long)Math.Round(
            absoluteStep * SamplesPerStep,
            MidpointRounding.AwayFromZero));
    }
}
