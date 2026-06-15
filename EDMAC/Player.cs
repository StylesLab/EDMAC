using System.Threading.Channels;

namespace EDMAC;

public sealed class Player : IDisposable
{
    private readonly Song song;
    private readonly SoundFontInstrument[] instruments;
    private readonly AudioEngine audioEngine;
    private readonly Channel<ScheduledTrackNote> noteQueue;
    private readonly Channel<string> diagnosticQueue;
    private bool disposed;

    public Player(Song song)
    {
        this.song = song;
        instruments = song.Tracks
            .Select(track => new SoundFontInstrument(track, song.SampleRate))
            .ToArray();

        audioEngine = new AudioEngine(instruments, song.SampleRate);
        noteQueue = Channel.CreateBounded<ScheduledTrackNote>(
            new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = true
            });

        diagnosticQueue = Channel.CreateBounded<string>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = true
            });
    }

    public async Task RunAsync()
    {
        PrintStartupDiagnostics();
        Console.WriteLine("Press the configured function keys to toggle tracks. Press Escape to stop.");

        using var cancellation = new CancellationTokenSource();
        var sequencer = new Sequencer(song, audioEngine, noteQueue.Writer);

        Task dispatchTask = StartDedicatedWorker(
            () => DispatchNotes(noteQueue.Reader, cancellation.Token),
            cancellation.Token);
        Task sequencerTask = StartDedicatedWorker(
            () => sequencer.Run(cancellation.Token),
            cancellation.Token);
        Task diagnosticTask = PrintDiagnosticsAsync(
            diagnosticQueue.Reader,
            cancellation.Token);

        audioEngine.Start();

        try
        {
            await ReadKeyboardAsync(cancellation);
        }
        finally
        {
            cancellation.Cancel();
            audioEngine.Stop();

            try
            {
                await Task.WhenAll(sequencerTask, dispatchTask, diagnosticTask);
            }
            catch (OperationCanceledException)
            {
            }
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

                diagnosticQueue.Writer.TryWrite(
                    $"{track.Name} NoteOn {item.Note.Note}");
            }
            while (reader.TryRead(out item));
        }
    }

    private static async Task PrintDiagnosticsAsync(
        ChannelReader<string> reader,
        CancellationToken cancellationToken)
    {
        await foreach (string message in reader.ReadAllAsync(cancellationToken))
        {
            Console.WriteLine(message);
        }
    }

    private async Task ReadKeyboardAsync(CancellationTokenSource cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(20, cancellation.Token);
                continue;
            }

            ConsoleKey key = Console.ReadKey(intercept: true).Key;
            if (key == ConsoleKey.Escape)
            {
                return;
            }

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
    }

    private void PrintStartupDiagnostics()
    {
        foreach (Track track in song.Tracks)
        {
            Console.WriteLine($"Track={track.Name}");
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
