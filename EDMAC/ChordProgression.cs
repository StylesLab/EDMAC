namespace EDMAC;

public sealed class ChordProgression
{
    public required string Name { get; init; }

    public required IReadOnlyList<Chord> Chords { get; init; }

    public required ConsoleKey Control { get; init; }
}
