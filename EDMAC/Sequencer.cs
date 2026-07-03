using System.Threading.Channels;

namespace EDMAC;

public sealed class Sequencer
{
    private readonly Song song;
    private readonly AudioEngine audioEngine;
    private readonly ChannelWriter<ScheduledTrackNote> writer;
    private readonly Action<long>? processPendingControls;
    private readonly long[] lastSectionSteps;
    private readonly long[] trackSectionVersions;

    internal Sequencer(
        Song song,
        AudioEngine audioEngine,
        ChannelWriter<ScheduledTrackNote> writer,
        Action<long>? processPendingControls = null)
    {
        this.song = song;
        this.audioEngine = audioEngine;
        this.writer = writer;
        this.processPendingControls = processPendingControls;
        lastSectionSteps = Enumerable.Repeat(-1L, song.Tracks.Count).ToArray();
        trackSectionVersions = Enumerable.Repeat(song.SectionVersion, song.Tracks.Count).ToArray();
    }

    public void Run(CancellationToken cancellationToken)
    {
        Thread.CurrentThread.Name ??= "EDMAC Sequencer";
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ProcessPosition(audioEngine.SamplePosition);
                Thread.Sleep(1);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    internal void ProcessPosition(long samplePosition)
    {
        processPendingControls?.Invoke(samplePosition);

        for (var trackIndex = 0; trackIndex < song.Tracks.Count; trackIndex++)
        {
            Track track = song.Tracks[trackIndex];
            Pattern timingPattern = track.TimingPattern;
            (long sectionStartSamplePosition, long sectionVersion) = song.GetSectionTiming();
            long sectionSamplePosition = Math.Max(0, samplePosition - sectionStartSamplePosition);
            long sectionStep = (long)Math.Floor(
                sectionSamplePosition / timingPattern.SamplesPerStep);

            if (trackSectionVersions[trackIndex] != sectionVersion)
            {
                lastSectionSteps[trackIndex] = -1;
                trackSectionVersions[trackIndex] = sectionVersion;
            }

            if (sectionStep <= lastSectionSteps[trackIndex])
            {
                continue;
            }

            for (long step = lastSectionSteps[trackIndex] + 1; step <= sectionStep; step++)
            {
                long stepSamplePosition = checked(
                    sectionStartSamplePosition + timingPattern.GetSamplePosition(step));
                ProcessStep(trackIndex, track, step, stepSamplePosition);
            }

            lastSectionSteps[trackIndex] = sectionStep;
        }
    }

    private void ProcessStep(
        int trackIndex,
        Track track,
        long sectionStep,
        long stepSamplePosition)
    {
        bool isTieStep = track.Patterns.Any(pattern => pattern.IsTieStep(sectionStep));

        if (!isTieStep)
        {
            writer.TryWrite(new ScheduledTrackNote(
                trackIndex,
                new ScheduledNote(
                    stepSamplePosition,
                    track.Channel,
                    0,
                    0),
                ScheduledNoteKind.NoteOffAll));
        }

        if (!track.Enabled)
        {
            return;
        }

        Chord? chord = song.GetChord(stepSamplePosition);

        foreach (Pattern pattern in track.Patterns)
        {
            char symbol = pattern.GetSymbol(sectionStep);
            if (symbol == Pattern.RestSymbol ||
                symbol == Pattern.TieSymbol ||
                !pattern.ShouldTrigger(sectionStep) ||
                !track.NoteMappings.TryGetValue(symbol, out NoteMapping? mapping))
            {
                continue;
            }

            for (var valueIndex = 0; valueIndex < mapping.Count; valueIndex++)
            {
                int note = mapping.Resolve(valueIndex, chord);
                if (note is < 0 or > 127)
                {
                    continue;
                }

                writer.TryWrite(new ScheduledTrackNote(
                    trackIndex,
                    new ScheduledNote(
                        stepSamplePosition,
                        track.Channel,
                        note,
                        track.Velocity),
                    pattern.PlaysToCompletion(sectionStep)
                        ? ScheduledNoteKind.NoteOnPlayToCompletion
                        : ScheduledNoteKind.NoteOn));
            }
        }
    }
}
