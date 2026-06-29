using System.Threading.Channels;
using EDMAC.Effects;
using NAudio.Wave;

namespace EDMAC;

public sealed class Player : IDisposable
{
    private readonly Song song;
    private readonly IInstrument[] instruments;
    private readonly AudioEngine audioEngine;
    private readonly IAudioEffect[][] effects;
    private readonly Channel<ScheduledTrackNote> noteQueue;
    private readonly bool printStartupDiagnostics;
    private readonly bool[] previousTrackEnabledStates;
    private bool disposed;

    public Player(Song song, bool printStartupDiagnostics = true)
    {
        this.song = song;
        this.printStartupDiagnostics = printStartupDiagnostics;
        song.InitializeTrackEnabledStates();

        instruments = song.Tracks
            .Select(track => InstrumentFactory.Create(track, song.SampleRate))
            .ToArray();
        effects = song.Tracks
            .Select(track => EffectFactory.CreateEffects(
                track.Effects,
                song.SampleRate,
                song.Bpm))
            .ToArray();
        previousTrackEnabledStates = new bool[song.Tracks.Count];

        audioEngine = new AudioEngine(song.Tracks, instruments, effects, song.SampleRate);
        audioEngine.StoppedUnexpectedly += OnAudioEngineStoppedUnexpectedly;
        noteQueue = Channel.CreateBounded<ScheduledTrackNote>(
            new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });

    }

    public event EventHandler<AudioEngineStoppedEventArgs>? PlaybackStoppedUnexpectedly;

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

    public void RenderToFile(
        string outputPath,
        double seconds,
        ConsoleKey? selectedControl = null,
        Func<Track, bool>? trackFilter = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        song.InitializeTrackEnabledStates(selectedControl, trackFilter);

        string fullOutputPath = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var sequencer = new Sequencer(song, audioEngine, noteQueue.Writer);
        long totalFrames = checked((long)Math.Round(seconds * audioEngine.WaveFormat.SampleRate));
        int blockAlign = audioEngine.WaveFormat.BlockAlign;
        int bufferFrames = 256;
        var buffer = new byte[bufferFrames * blockAlign];

        using var writer = new WaveFileWriter(fullOutputPath, audioEngine.WaveFormat);

        long renderedFrames = 0;
        while (renderedFrames < totalFrames)
        {
            sequencer.ProcessPosition(audioEngine.SamplePosition);
            DispatchPendingNotes();

            int frames = (int)Math.Min(bufferFrames, totalFrames - renderedFrames);
            int bytes = frames * blockAlign;
            audioEngine.Read(buffer, 0, bytes);
            writer.Write(buffer, 0, bytes);
            renderedFrames += frames;
        }

        DispatchPendingNotes();
    }

    public void RenderArrangementToFile(
        string outputPath,
        Func<Track, bool>? trackFilter = null)
    {
        if (song.Arrangement.Count == 0)
        {
            throw new InvalidOperationException(
                "The song does not define an arrangement.");
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var sequencer = new Sequencer(song, audioEngine, noteQueue.Writer);
        int blockAlign = audioEngine.WaveFormat.BlockAlign;
        int bufferFrames = 256;
        var buffer = new byte[bufferFrames * blockAlign];

        using var writer = new WaveFileWriter(fullOutputPath, audioEngine.WaveFormat);

        foreach (ArrangementStep step in song.Arrangement)
        {
            ApplyRenderTrackControl(step.Control, trackFilter);

            long segmentFrames = checked((long)Math.Round(
                step.Bars * 4.0 * 60.0 / song.Bpm * audioEngine.WaveFormat.SampleRate));
            long renderedFrames = 0;

            while (renderedFrames < segmentFrames)
            {
                sequencer.ProcessPosition(audioEngine.SamplePosition);
                DispatchPendingNotes();

                int frames = (int)Math.Min(bufferFrames, segmentFrames - renderedFrames);
                int bytes = frames * blockAlign;
                audioEngine.Read(buffer, 0, bytes);
                writer.Write(buffer, 0, bytes);
                renderedFrames += frames;
            }
        }

        DispatchPendingNotes();
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
            if (!track.HasControl(key))
            {
                continue;
            }

            ApplyTrackControl(key);
            break;
        }
    }

    private void ApplyTrackControl(ConsoleKey key)
    {
        for (var trackIndex = 0; trackIndex < song.Tracks.Count; trackIndex++)
        {
            previousTrackEnabledStates[trackIndex] = song.Tracks[trackIndex].Enabled;
        }

        song.ApplyTrackControl(key);

        for (var trackIndex = 0; trackIndex < song.Tracks.Count; trackIndex++)
        {
            Track track = song.Tracks[trackIndex];
            if (previousTrackEnabledStates[trackIndex] && !track.Enabled)
            {
                noteQueue.Writer.TryWrite(new ScheduledTrackNote(
                    trackIndex,
                    new ScheduledNote(
                        audioEngine.SamplePosition,
                        track.Channel,
                        0,
                        0),
                    ScheduledNoteKind.NoteOffAllIncludingPlayToCompletion));
            }
        }

        ConsoleUi.Control($"Tracks={key} enabled={FormatEnabledTracks()}");
    }

    private void ApplyRenderTrackControl(
        ConsoleKey control,
        Func<Track, bool>? trackFilter)
    {
        for (var trackIndex = 0; trackIndex < song.Tracks.Count; trackIndex++)
        {
            previousTrackEnabledStates[trackIndex] = song.Tracks[trackIndex].Enabled;
        }

        song.ApplyTrackControl(control, trackFilter);

        for (var trackIndex = 0; trackIndex < song.Tracks.Count; trackIndex++)
        {
            Track track = song.Tracks[trackIndex];
            if (previousTrackEnabledStates[trackIndex] && !track.Enabled)
            {
                noteQueue.Writer.TryWrite(new ScheduledTrackNote(
                    trackIndex,
                    new ScheduledNote(
                        audioEngine.SamplePosition,
                        track.Channel,
                        0,
                        0),
                    ScheduledNoteKind.NoteOffAllIncludingPlayToCompletion));
            }
        }
    }

    public (bool IsRecording, string? Path) ToggleRecording(string recordingsDirectory)
    {
        if (audioEngine.IsRecording)
        {
            return (false, audioEngine.StopRecording());
        }

        return (true, audioEngine.StartRecording(recordingsDirectory));
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

    private void OnAudioEngineStoppedUnexpectedly(object? sender, AudioEngineStoppedEventArgs args)
    {
        PlaybackStoppedUnexpectedly?.Invoke(this, args);
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
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

        try
        {
            while (reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out ScheduledTrackNote item))
                {
                    DispatchNote(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void DispatchPendingNotes()
    {
        while (noteQueue.Reader.TryRead(out ScheduledTrackNote item))
        {
            DispatchNote(item);
        }
    }

    private void DispatchNote(ScheduledTrackNote item)
    {
        Track track = song.Tracks[item.TrackIndex];

        if (item.Kind is ScheduledNoteKind.NoteOffAll or
            ScheduledNoteKind.NoteOffAllIncludingPlayToCompletion)
        {
            instruments[item.TrackIndex].StopChannel(
                item.Note.Channel,
                item.Kind == ScheduledNoteKind.NoteOffAllIncludingPlayToCompletion);
            return;
        }

        if (!track.Enabled)
        {
            return;
        }

        instruments[item.TrackIndex].NoteOn(
            item.Note.Channel,
            item.Note.Note,
            item.Note.Velocity,
            item.Kind == ScheduledNoteKind.NoteOnPlayToCompletion);
    }

    private void PrintStartupDiagnostics()
    {
        PrintControlDiagnostics();

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
            ConsoleUi.KeyValue("Controls", FormatControls(track.Controls));
            ConsoleUi.KeyValue("Enabled", track.Enabled.ToString());
        }

        if (song.ActiveProgression is not null)
        {
            ConsoleUi.KeyValue("Progression", song.ActiveProgression.Name);
        }
    }

    private void PrintControlDiagnostics()
    {
        ConsoleUi.KeyValue("StartOn", song.StartOnControl?.ToString() ?? "(none)");
        ConsoleUi.KeyValue("ActiveTracks", song.ActiveTrackControl?.ToString() ?? "(all)");

        ConsoleKey[] trackControls = song.Tracks
            .SelectMany(track => track.Controls)
            .Distinct()
            .Order()
            .ToArray();

        ConsoleUi.KeyValue(
            "TrackControls",
            trackControls.Length == 0
                ? "(none - all tracks always enabled)"
                : string.Join(",", trackControls));

        foreach (ConsoleKey control in trackControls)
        {
            string trackNames = string.Join(
                ",",
                song.Tracks
                    .Where(track => track.HasControl(control))
                    .Select(track => track.Name));
            ConsoleUi.KeyValue($"{control}", trackNames);
        }

        ConsoleKey[] progressionControls = song.Progressions
            .Select(progression => progression.Control)
            .Distinct()
            .Order()
            .ToArray();

        ConsoleUi.KeyValue(
            "ProgressionControls",
            progressionControls.Length == 0
                ? "(none)"
                : string.Join(",", progressionControls));
    }

    private static string FormatControls(IReadOnlySet<ConsoleKey> controls)
    {
        return controls.Count == 0
            ? "(always)"
            : string.Join(",", controls.Order());
    }

    private string FormatEnabledTracks()
    {
        return string.Join(
            ",",
            song.Tracks
                .Where(track => track.Enabled)
                .Select(track => track.Name));
    }
}
