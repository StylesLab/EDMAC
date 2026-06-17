namespace EDMAC;

public sealed class Pattern
{
    public const char RestSymbol = '.';
    public const char TieSymbol = '>';

    public Pattern(string source, int beats, int sampleRate, double bpm)
    {
        Steps = new string(source.Where(character => character != '|').ToArray());
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

    public long GetSamplePosition(long absoluteStep)
    {
        return checked((long)Math.Round(
            absoluteStep * SamplesPerStep,
            MidpointRounding.AwayFromZero));
    }
}
