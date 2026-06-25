namespace EDMAC;

public sealed record ScheduledNote(
    long SamplePosition,
    int Channel,
    int Note,
    int Velocity);

internal readonly record struct ScheduledTrackNote(
    int TrackIndex,
    ScheduledNote Note,
    ScheduledNoteKind Kind);

internal enum ScheduledNoteKind
{
    NoteOffAll,
    NoteOffAllIncludingPlayToCompletion,
    NoteOn,
    NoteOnPlayToCompletion
}
