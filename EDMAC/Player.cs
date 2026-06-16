using System.Threading.Channels;
using EDMAC.Effects;

namespace EDMAC;

public sealed class Player : IDisposable
{
    private readonly Song song;
    private readonly IInstrument[] instruments;
    private readonly AudioEngine audioEngine;
    private readonly IAudioEffect[][] effects;
    private readonly Channel<ScheduledTrackNote> noteQueue;
    private readonly bool printStartupDiagnostics;
    private bool disposed;

    public Player(Song song, bool printStartupDiagnostics = true)
    {
        this.song = song;
        this.printStartupDiagnostics = printStartupDiagnostics;
        foreach (Track track in song.Tracks)
        {
            track.InitializeEnabledState();
        }

        instruments = song.Tracks
            .Select(track => InstrumentFactory.Create(track, song.SampleRate))
            .ToArray();
        effects = song.Tracks
            .Select(track => EffectFactory.CreateEffects(
                track.Effects,
                song.SampleRate,
                song.Bpm))
            .ToArray();

        audioEngine = new AudioEngine(instruments, effects, song.SampleRate);
        noteQueue = Channel.CreateBounded<ScheduledTrackNote>(
            new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = true
            });

    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (printStartupDiagnostics)
        {
            PrintStartupDiagnostics();
        }

        using var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sequencer = new Sequencer(song, audioEngine, noteQueue.Writer);

        Task dispatchTask = StartDedicatedWorker(
            () => DispatchNotes(noteQueue.Reader, playbackCancellation.Token),
            playbackCancellation.Token);
        Task sequencerTask = StartDedicatedWorker(
            () => sequencer.Run(playbackCancellation.Token),
            playbackCancellation.Token);
        audioEngine.Start();

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            playbackCancellation.Cancel();
            audioEngine.Stop();

            try
            {
                await Task.WhenAll(sequencerTask, dispatchTask);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void HandleKey(ConsoleKey key)
    {
        ChordProgression? progression = song.CycleProgression(key);
        if (progression is not null)
        {
            ConsoleUi.Control($"Progression={progression.Name}");
        }

        for (var trackIndex = 0; trackIndex < song.Tracks.Count; trackIndex++)
        {
            Track track = song.Tracks[trackIndex];
            if (track.Control != key)
            {
                continue;
            }

            bool enabled = track.Toggle();
            if (!enabled)
            {
                instruments[trackIndex].StopChannel(track.Channel);
            }

            ConsoleUi.Track(track.Name, enabled ? "enabled" : "muted");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        audioEngine.Dispose();
    }

    private static Task StartDedicatedWorker(
        Action action,
        CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            action,
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void DispatchNotes(
        ChannelReader<ScheduledTrackNote> reader,
        CancellationToken cancellationToken)
    {
        Thread.CurrentThread.Name ??= "EDMAC MIDI Dispatcher";
        Thread.CurrentThread.Priority = ThreadPriority.Highest;

        try
        {
            while (reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out ScheduledTrackNote item))
                {
                    Track track = song.Tracks[item.TrackIndex];

                    if (item.Kind == ScheduledNoteKind.NoteOffAll)
                    {
                        instruments[item.TrackIndex].StopChannel(item.Note.Channel);
                        continue;
                    }

                    if (!track.Enabled)
                    {
                        continue;
                    }

                    instruments[item.TrackIndex].NoteOn(
                        item.Note.Channel,
                        item.Note.Note,
                        item.Note.Velocity);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PrintStartupDiagnostics()
    {
        foreach (Track track in song.Tracks)
        {
            ConsoleUi.KeyValue("Track", track.Name);
            ConsoleUi.KeyValue("Instrument", track.InstrumentKind.ToString());
            ConsoleUi.KeyValue("Patterns", track.Patterns.Count.ToString());
            ConsoleUi.KeyValue(
                "PatternLengths",
                string.Join(",", track.Patterns.Select(pattern => pattern.Length)));
            ConsoleUi.KeyValue("SamplesPerStep", track.TimingPattern.SamplesPerStep.ToString());
            ConsoleUi.KeyValue("Amp", track.Amp.ToString());
            ConsoleUi.KeyValue("Effects", track.Effects.Count.ToString());
            ConsoleUi.KeyValue("Enabled", track.Enabled.ToString());
        }

        if (song.ActiveProgression is not null)
        {
            ConsoleUi.KeyValue("Progression", song.ActiveProgression.Name);
        }
    }
}
