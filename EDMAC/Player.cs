using System.Threading.Channels;
using EDMAC.Effects;
using NAudio.Wave;

namespace EDMAC;

public sealed class Player : IDisposable
{
    private const int BeatsPerBar = 4;
    private const double ControlCatchWindowMilliseconds = 120;

    private readonly Song song;
    private readonly IInstrument[] instruments;
    private readonly AudioEngine audioEngine;
    private readonly IAudioEffect[][] effects;
    private readonly Channel<ScheduledTrackNote> noteQueue;
    private readonly bool printStartupDiagnostics;
    private readonly bool[] previousTrackEnabledStates;
    private readonly object pendingControlLock = new();
    private readonly double controlQuantizeSamples;
    private readonly double controlCatchWindowSamples;
    private PendingTrackControl? pendingTrackControl;
    private PendingProgressionControl? pendingProgressionControl;
    private bool disposed;

    public Player(Song song, bool printStartupDiagnostics = true)
    {
        this.song = song;
        this.printStartupDiagnostics = printStartupDiagnostics;
        song.ResetPlaybackPosition();
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
        controlQuantizeSamples = song.SampleRate * 60.0 / song.Bpm * BeatsPerBar;
        controlCatchWindowSamples = song.SampleRate * ControlCatchWindowMilliseconds / 1000.0;

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
        using PlaybackPerformanceScope performanceScope = PlaybackPerformanceScope.Start();

        if (printStartupDiagnostics)
        {
            PrintStartupDiagnostics();
        }

        using var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sequencer = new Sequencer(
            song,
            audioEngine,
            noteQueue.Writer,
            ProcessPendingControls);

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
        if (song.HasProgressionControl(key))
        {
            QueueProgressionControl(key);
        }

        for (var trackIndex = 0; trackIndex < song.Tracks.Count; trackIndex++)
        {
            Track track = song.Tracks[trackIndex];
            if (!track.HasControl(key))
            {
                continue;
            }

            QueueTrackControl(key);
            break;
        }
    }

    private void QueueTrackControl(ConsoleKey key)
    {
        long samplePosition = audioEngine.SamplePosition;
        long targetSamplePosition = GetNextControlSamplePosition(samplePosition);

        lock (pendingControlLock)
        {
            pendingTrackControl = new PendingTrackControl(key, targetSamplePosition);
        }
    }

    private void QueueProgressionControl(ConsoleKey key)
    {
        long samplePosition = audioEngine.SamplePosition;
        long targetSamplePosition = GetNextControlSamplePosition(samplePosition);

        lock (pendingControlLock)
        {
            pendingProgressionControl = new PendingProgressionControl(key, targetSamplePosition);
        }
    }

    private long GetNextControlSamplePosition(long samplePosition)
    {
        double currentStep = Math.Floor(samplePosition / controlQuantizeSamples) *
            controlQuantizeSamples;
        double samplesSinceCurrentStep = samplePosition - currentStep;
        if (currentStep > 0 &&
            samplesSinceCurrentStep <= controlCatchWindowSamples)
        {
            return checked((long)Math.Round(
                currentStep,
                MidpointRounding.AwayFromZero));
        }

        double nextStep = Math.Ceiling((samplePosition + 1) / controlQuantizeSamples) *
            controlQuantizeSamples;
        return checked((long)Math.Round(nextStep, MidpointRounding.AwayFromZero));
    }

    private void ProcessPendingControls(long samplePosition)
    {
        PendingProgressionControl? progression;
        PendingTrackControl? track;

        lock (pendingControlLock)
        {
            progression = pendingProgressionControl;
            if (progression is not null &&
                progression.Value.SamplePosition <= samplePosition)
            {
                pendingProgressionControl = null;
            }

            track = pendingTrackControl;
            if (track is not null &&
                track.Value.SamplePosition <= samplePosition)
            {
                pendingTrackControl = null;
            }
        }

        bool appliedProgression = progression is not null &&
            progression.Value.SamplePosition <= samplePosition;

        if (appliedProgression)
        {
            ApplyProgressionControl(
                progression!.Value.Control,
                progression.Value.SamplePosition);
        }

        if (track is not null &&
            track.Value.SamplePosition <= samplePosition)
        {
            ApplyTrackControl(
                track.Value.Control,
                track.Value.SamplePosition,
                triggerEnabledTracks: !appliedProgression);
        }
    }

    private void ApplyProgressionControl(ConsoleKey key, long samplePosition)
    {
        ChordProgression? progression = song.CycleProgression(key, samplePosition);
        if (progression is null)
        {
            return;
        }

        StopAllTracks(samplePosition);
        ConsoleUi.Control($"Progression={progression.Name}");
    }

    private void ApplyTrackControl(
        ConsoleKey key,
        long samplePosition,
        bool triggerEnabledTracks = true)
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
                        samplePosition,
                        track.Channel,
                        0,
                        0),
                    ScheduledNoteKind.NoteOffAllIncludingPlayToCompletion));
            }

            if (triggerEnabledTracks &&
                !previousTrackEnabledStates[trackIndex] &&
                track.Enabled)
            {
                StopCurrentStepIfNeeded(trackIndex, track, samplePosition);
                TriggerCurrentStep(trackIndex, track, samplePosition);
            }
        }

        ConsoleUi.Control($"Tracks={key} enabled={FormatEnabledTracks()}");
    }

    private void StopCurrentStepIfNeeded(int trackIndex, Track track, long samplePosition)
    {
        Pattern timingPattern = track.TimingPattern;
        long sectionSamplePosition = song.GetSectionSamplePosition(samplePosition);
        long sectionStep = (long)Math.Floor(sectionSamplePosition / timingPattern.SamplesPerStep);
        bool isTieStep = track.Patterns.Any(pattern => pattern.IsTieStep(sectionStep));

        if (isTieStep)
        {
            return;
        }

        noteQueue.Writer.TryWrite(new ScheduledTrackNote(
            trackIndex,
            new ScheduledNote(
                samplePosition,
                track.Channel,
                0,
                0),
            ScheduledNoteKind.NoteOffAll));
    }

    private void TriggerCurrentStep(int trackIndex, Track track, long samplePosition)
    {
        Pattern timingPattern = track.TimingPattern;
        long sectionSamplePosition = song.GetSectionSamplePosition(samplePosition);
        long sectionStep = (long)Math.Floor(sectionSamplePosition / timingPattern.SamplesPerStep);
        Chord? chord = song.GetChord(samplePosition);

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

                noteQueue.Writer.TryWrite(new ScheduledTrackNote(
                    trackIndex,
                    new ScheduledNote(
                        samplePosition,
                        track.Channel,
                        note,
                        track.Velocity),
                    pattern.PlaysToCompletion(sectionStep)
                        ? ScheduledNoteKind.NoteOnPlayToCompletion
                        : ScheduledNoteKind.NoteOn));
            }
        }
    }

    private void StopAllTracks(long samplePosition)
    {
        for (var trackIndex = 0; trackIndex < song.Tracks.Count; trackIndex++)
        {
            Track track = song.Tracks[trackIndex];
            noteQueue.Writer.TryWrite(new ScheduledTrackNote(
                trackIndex,
                new ScheduledNote(
                    samplePosition,
                    track.Channel,
                    0,
                    0),
                ScheduledNoteKind.NoteOffAllIncludingPlayToCompletion));
        }
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

    private readonly record struct PendingTrackControl(
        ConsoleKey Control,
        long SamplePosition);

    private readonly record struct PendingProgressionControl(
        ConsoleKey Control,
        long SamplePosition);

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
