namespace EDMAC;

public sealed class Song
{
    private ProgressionSelection progressionSelection = new(0, 0, 0);
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
            : Progressions[Volatile.Read(ref progressionSelection).Index];

    public long SectionStartSamplePosition =>
        Volatile.Read(ref progressionSelection).StartSamplePosition;

    public long SectionVersion =>
        Volatile.Read(ref progressionSelection).Version;

    public ConsoleKey? ActiveTrackControl =>
        HasTrackControls
            ? (ConsoleKey)Volatile.Read(ref activeTrackControl)
            : null;

    public bool HasTrackControls => Tracks.Any(track => track.HasControls);

    public Chord? GetChord(long samplePosition)
    {
        ProgressionSelection selection = Volatile.Read(ref progressionSelection);
        if (Progressions.Count == 0)
        {
            return null;
        }

        ChordProgression progression = Progressions[selection.Index];
        long sectionSamplePosition = Math.Max(0, samplePosition - selection.StartSamplePosition);
        int sectionQuarter = (int)Math.Floor((sectionSamplePosition + 0.5) / SamplesPerChordQuarter);
        return progression.GetChordAtQuarter(sectionQuarter);
    }

    public (long StartSamplePosition, long Version) GetSectionTiming()
    {
        ProgressionSelection selection = Volatile.Read(ref progressionSelection);
        return (selection.StartSamplePosition, selection.Version);
    }

    public long GetSectionSamplePosition(long samplePosition)
    {
        ProgressionSelection selection = Volatile.Read(ref progressionSelection);
        return Math.Max(0, samplePosition - selection.StartSamplePosition);
    }

    public ChordProgression? CycleProgression(ConsoleKey control, long samplePosition)
    {
        if (Progressions.Count == 0)
        {
            return null;
        }

        ProgressionSelection current = Volatile.Read(ref progressionSelection);

        for (var offset = 1; offset <= Progressions.Count; offset++)
        {
            int candidate = (current.Index + offset) % Progressions.Count;
            if (Progressions[candidate].Control == control)
            {
                var next = new ProgressionSelection(
                    candidate,
                    samplePosition,
                    current.Version + 1);
                Volatile.Write(ref progressionSelection, next);
                return Progressions[candidate];
            }
        }

        return null;
    }

    public void ResetPlaybackPosition()
    {
        ProgressionSelection current = Volatile.Read(ref progressionSelection);
        var reset = new ProgressionSelection(
            current.Index,
            0,
            current.Version + 1);
        Volatile.Write(ref progressionSelection, reset);
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

    public bool HasProgressionControl(ConsoleKey control)
    {
        return Progressions.Any(progression => progression.Control == control);
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

    private sealed record ProgressionSelection(
        int Index,
        long StartSamplePosition,
        long Version);
}

public sealed class ArrangementStep
{
    public required ConsoleKey Control { get; init; }

    public required int Bars { get; init; }
}
