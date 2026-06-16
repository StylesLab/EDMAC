namespace EDMAC;

public sealed class Song
{
    private int activeProgressionIndex;

    public required double Bpm { get; init; }

    public required int SampleRate { get; init; }

    public required IReadOnlyList<ChordProgression> Progressions { get; init; }

    public required IReadOnlyList<Track> Tracks { get; init; }

    public double SamplesPerChordQuarter => SampleRate * 60.0 / Bpm;

    public ChordProgression? ActiveProgression =>
        Progressions.Count == 0
            ? null
            : Progressions[Volatile.Read(ref activeProgressionIndex)];

    public Chord? GetChord(long samplePosition)
    {
        ChordProgression? progression = ActiveProgression;
        if (progression is null)
        {
            return null;
        }

        int absoluteQuarter = (int)Math.Floor(samplePosition / SamplesPerChordQuarter);
        return progression.GetChordAtQuarter(absoluteQuarter);
    }

    public ChordProgression? CycleProgression(ConsoleKey control)
    {
        if (Progressions.Count == 0)
        {
            return null;
        }

        int current = Volatile.Read(ref activeProgressionIndex);

        for (var offset = 1; offset <= Progressions.Count; offset++)
        {
            int candidate = (current + offset) % Progressions.Count;
            if (Progressions[candidate].Control == control)
            {
                Interlocked.Exchange(ref activeProgressionIndex, candidate);
                return Progressions[candidate];
            }
        }

        return null;
    }
}
