using System.Threading.Channels;

namespace EDMAC;

public sealed class Sequencer
{
    private readonly Song song;
    private readonly AudioEngine audioEngine;
    private readonly ChannelWriter<ScheduledTrackNote> writer;
    private readonly long[] lastAbsoluteSteps;

    internal Sequencer(
        Song song,
        AudioEngine audioEngine,
        ChannelWriter<ScheduledTrackNote> writer)
    {
        this.song = song;
        this.audioEngine = audioEngine;
        this.writer = writer;
        lastAbsoluteSteps = Enumerable.Repeat(-1L, song.Tracks.Count).ToArray();
    }

    public void Run(CancellationToken cancellationToken)
    {
        Thread.CurrentThread.Name ??= "EDMAC Sequencer";
        Thread.CurrentThread.Priority = ThreadPriority.Highest;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long samplePosition = audioEngine.SamplePosition;

                for (var trackIndex = 0; trackIndex < song.Tracks.Count; trackIndex++)
                {
                    Track track = song.Tracks[trackIndex];
                    Pattern timingPattern = track.TimingPattern;
                    long absoluteStep = (long)Math.Floor(
                        samplePosition / timingPattern.SamplesPerStep);

                    if (absoluteStep == lastAbsoluteSteps[trackIndex])
                    {
                        continue;
                    }

                    lastAbsoluteSteps[trackIndex] = absoluteStep;
                    long stepSamplePosition = timingPattern.GetSamplePosition(absoluteStep);
                    bool isTieStep = track.Patterns.Any(pattern => pattern.IsTieStep(absoluteStep));

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
                        continue;
                    }

                    Chord? chord = song.GetChord(stepSamplePosition);

                    foreach (Pattern pattern in track.Patterns)
                    {
                        char symbol = pattern.GetSymbol(absoluteStep);
                        if (symbol == Pattern.RestSymbol ||
                            symbol == Pattern.TieSymbol ||
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
                                ScheduledNoteKind.NoteOn));
                        }
                    }
                }

                Thread.Sleep(1);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }
}
