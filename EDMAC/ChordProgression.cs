namespace EDMAC;

public sealed class ChordProgression
{
    public const int DefaultChordDurationQuarters = 4;

    public required string Name { get; init; }

    public required IReadOnlyList<ChordProgressionStep> Steps { get; init; }

    public required ConsoleKey Control { get; init; }

    public int Transpose { get; init; }

    public int TotalDurationQuarters => Steps.Sum(step => step.DurationQuarters);

    public Chord GetChordAtQuarter(int quarter)
    {
        int position = Mod(quarter, TotalDurationQuarters);

        for (var index = Steps.Count - 1; index >= 0; index--)
        {
            ChordProgressionStep step = Steps[index];
            if (position >= step.StartQuarter)
            {
                return step.Chord;
            }
        }

        return Steps[0].Chord;
    }

    private static int Mod(int value, int modulo)
    {
        int result = value % modulo;
        return result < 0 ? result + modulo : result;
    }
}
