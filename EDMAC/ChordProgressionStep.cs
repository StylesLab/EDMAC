namespace EDMAC;

public sealed class ChordProgressionStep
{
    public required Chord Chord { get; init; }

    public required int DurationQuarters { get; init; }

    public required int StartQuarter { get; init; }

    public string DisplayName =>
        DurationQuarters == ChordProgression.DefaultChordDurationQuarters
            ? Chord.Name
            : $"{Chord.Name}:{DurationQuarters}";
}
