using System.Threading.Channels;

namespace EDMAC;

public sealed class Player : IDisposable
{
    private readonly Song song;
    private readonly IInstrument[] instruments;
    private readonly AudioEngine audioEngine;
    private readonly Channel<ScheduledTrackNote> noteQueue;
    private readonly bool printStartupDiagnostics;
    private bool disposed;

    public Player(Song song, bool printStartupDiagnostics = true)
    {
        this.song = song;
        this.printStartupDiagnostics = printStartupDiagnostics;
        instruments = song.Tracks
            .Select(track => InstrumentFactory.Create(track, song.SampleRate))
            .ToArray();

        audioEngine = new AudioEngine(instruments, song.SampleRate);
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
            Console.WriteLine($"Progression={progression.Name}");
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

            Console.WriteLine($"{track.Name} {(enabled ? "enabled" : "muted")}");
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

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!reader.TryRead(out ScheduledTrackNote item))
            {
                Thread.SpinWait(64);
                continue;
            }

            do
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
            while (reader.TryRead(out item));
        }
    }

    private void PrintStartupDiagnostics()
    {
        foreach (Track track in song.Tracks)
        {
            Console.WriteLine($"Track={track.Name}");
            Console.WriteLine($"Instrument={track.InstrumentKind}");
            Console.WriteLine($"Patterns={track.Patterns.Count}");
            Console.WriteLine(
                $"PatternLengths={string.Join(",", track.Patterns.Select(pattern => pattern.Length))}");
            Console.WriteLine($"SamplesPerStep={track.TimingPattern.SamplesPerStep}");
            Console.WriteLine($"Amp={track.Amp}");
        }

        if (song.ActiveProgression is not null)
        {
            Console.WriteLine($"Progression={song.ActiveProgression.Name}");
        }
    }
}
