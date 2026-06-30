namespace EDMAC;

public sealed class Song
{
    private int activeProgressionIndex;
    private int activeTrackControl;

    public required double Bpm { get; init; }

    public required int SampleRate { get; init; }

    public required ConsoleKey? StartOnControl { get; init; }

    public required IReadOnlyList<ChordProgression> Progressions { get; init; }

    public required IReadOnlyList<ArrangementStep> Arrangement { get; init; }

    public required IReadOnlyList<Track> Tracks { get; init; }

    public double SamplesPerChordQuarter => SampleRate * 60.0 / Bpm;

    public ChordProgression? ActiveProgression =>
        Progressions.Count == 0
            ? null
            : Progressions[Volatile.Read(ref activeProgressionIndex)];

    public ConsoleKey? ActiveTrackControl =>
        HasTrackControls
            ? (ConsoleKey)Volatile.Read(ref activeTrackControl)
            : null;

    public bool HasTrackControls => Tracks.Any(track => track.HasControls);

    public Chord? GetChord(long samplePosition)
    {
        ChordProgression? progression = ActiveProgression;
        if (progression is null)
        {
            return null;
        }

        int absoluteQuarter = (int)Math.Floor((samplePosition + 0.5) / SamplesPerChordQuarter);
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

    public void InitializeTrackEnabledStates(
        ConsoleKey? selectedControl = null,
        Func<Track, bool>? trackFilter = null)
    {
        if (!HasTrackControls)
        {
            foreach (Track track in Tracks)
            {
                track.SetEnabled(trackFilter?.Invoke(track) ?? true);
            }

            return;
        }

        ApplyTrackControl(selectedControl ?? StartOnControl!.Value, trackFilter);
    }

    public bool HasTrackControl(ConsoleKey control)
    {
        return Tracks.Any(track => track.HasControl(control));
    }

    public void ApplyTrackControl(ConsoleKey control, Func<Track, bool>? trackFilter = null)
    {
        Interlocked.Exchange(ref activeTrackControl, (int)control);

        foreach (Track track in Tracks)
        {
            bool enabledByControl = !track.HasControls || track.HasControl(control);
            bool enabledByFilter = trackFilter?.Invoke(track) ?? true;
            track.SetEnabled(enabledByControl && enabledByFilter);
        }
    }
}

public sealed class ArrangementStep
{
    public required ConsoleKey Control { get; init; }

    public required int Bars { get; init; }
}
