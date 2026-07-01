using System.Threading.Channels;

namespace EDMAC;

public sealed class HotReloadingPlayer
{
    private readonly string yamlPath;
    private readonly object playerLock = new();
    private readonly SemaphoreSlim transportLock = new(1, 1);
    private readonly Channel<bool> reloadSignals;
    private readonly Channel<AudioEngineStoppedEventArgs> playbackStopSignals;
    private Player? currentPlayer;
    private CancellationTokenSource? currentPlaybackCancellation;
    private Task? currentPlaybackTask;
    private Song? currentSong;
    private MidiControlInput? midiInput;
    private bool hasLoadedOnce;
    private bool paused = true;

    public HotReloadingPlayer(string yamlPath)
    {
        this.yamlPath = Path.GetFullPath(yamlPath);
        reloadSignals = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        playbackStopSignals = Channel.CreateBounded<AudioEngineStoppedEventArgs>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

    public async Task RunAsync()
    {
        ConsoleUi.Banner();
        ConsoleUi.Info("Press Space to start. Space pauses/resumes. H restart. R record. Q or Escape quit.");
        ConsoleUi.Info("Function keys select track groups and progressions.");
        ConsoleUi.Info("MIDI note 62 toggles play/pause; notes 64,65,67,69,71,72,74,76,77,79 select F1-F10.");
        ConsoleUi.Info("Editing the YAML file will reload it and restart playback.");

        using var hostCancellation = new CancellationTokenSource();
        using FileSystemWatcher watcher = CreateWatcher();
        midiInput = StartMidiInput();

        await ReloadAsync();
        watcher.EnableRaisingEvents = true;

        Task keyboardTask = ReadKeyboardAsync(hostCancellation);
        Task reloadTask = ProcessReloadsAsync(hostCancellation.Token);
        Task playbackStopTask = ProcessPlaybackStopsAsync(hostCancellation.Token);

        await keyboardTask;
        hostCancellation.Cancel();

        try
        {
            await Task.WhenAll(reloadTask, playbackStopTask);
        }
        catch (OperationCanceledException)
        {
        }

        midiInput?.Dispose();
        midiInput = null;

        await transportLock.WaitAsync();
        try
        {
            await StopCurrentPlayerAsync();
            paused = true;
        }
        finally
        {
            transportLock.Release();
        }
    }

    private MidiControlInput? StartMidiInput()
    {
        try
        {
            MidiControlInput input = MidiControlInput.Start(
                HandleMidiControl,
                HandleMidiPlayPause);
            if (input.DeviceNames.Count == 0)
            {
                ConsoleUi.Info("No MIDI input devices found.");
            }
            else
            {
                ConsoleUi.Success($"MIDI input: {string.Join(", ", input.DeviceNames)}");
            }

            foreach (string failedDeviceMessage in input.FailedDeviceMessages)
            {
                ConsoleUi.Warning($"MIDI input skipped: {failedDeviceMessage}");
            }

            return input;
        }
        catch (Exception exception)
        {
            ConsoleUi.Warning($"MIDI input unavailable: {exception.Message}");
            return null;
        }
    }

    private FileSystemWatcher CreateWatcher()
    {
        string directory = Path.GetDirectoryName(yamlPath)!;
        string fileName = Path.GetFileName(yamlPath);

        var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter =
                NotifyFilters.LastWrite |
                NotifyFilters.Size |
                NotifyFilters.CreationTime |
                NotifyFilters.FileName
        };

        watcher.Changed += OnSongFileChanged;
        watcher.Created += OnSongFileChanged;
        watcher.Renamed += OnSongFileChanged;
        return watcher;
    }

    private void OnSongFileChanged(object sender, FileSystemEventArgs args)
    {
        reloadSignals.Writer.TryWrite(true);
    }

    private async Task ProcessReloadsAsync(CancellationToken cancellationToken)
    {
        await foreach (bool _ in reloadSignals.Reader.ReadAllAsync(cancellationToken))
        {
            await Task.Delay(250, cancellationToken);

            while (reloadSignals.Reader.TryRead(out bool _))
            {
            }

            await ReloadAsync();
        }
    }

    private async Task ProcessPlaybackStopsAsync(CancellationToken cancellationToken)
    {
        await foreach (AudioEngineStoppedEventArgs args in
            playbackStopSignals.Reader.ReadAllAsync(cancellationToken))
        {
            if (paused || currentSong is null)
            {
                continue;
            }

            await transportLock.WaitAsync(cancellationToken);
            try
            {
                if (paused || currentSong is null)
                {
                    continue;
                }

                string reason = args.Exception is null
                    ? "audio output stopped"
                    : $"audio output stopped:{Environment.NewLine}{args.Exception}";
                ConsoleUi.Warning($"{reason}; restarting playback.");

                await StopCurrentPlayerAsync();

                if (!paused && currentSong is not null)
                {
                    StartPlayer(currentSong, printStartupDiagnostics: false);
                }
            }
            finally
            {
                transportLock.Release();
            }
        }
    }

    private async Task ReloadAsync()
    {
        Song song;

        try
        {
            song = SongLoader.Load(yamlPath, AudioEngine.DefaultSampleRate);
        }
        catch (Exception exception)
        {
            ConsoleUi.Error($"Reload failed: {exception.Message}");
            return;
        }

        await transportLock.WaitAsync();
        try
        {
            currentSong = song;

            if (paused)
            {
                PrintControlSummary(song);
                hasLoadedOnce = true;
                ConsoleUi.Success($"Loaded {Path.GetFileName(yamlPath)}. Press Space to play.");
                return;
            }

            await StopCurrentPlayerAsync();

            try
            {
                StartPlayer(song, printStartupDiagnostics: !hasLoadedOnce);

                PrintControlSummary(song);
                hasLoadedOnce = true;
                ConsoleUi.Success($"Reloaded {Path.GetFileName(yamlPath)}");
            }
            catch (Exception exception)
            {
                ConsoleUi.Error($"Reload failed: {exception.Message}");
            }
        }
        finally
        {
            transportLock.Release();
        }
    }

    private void StartPlayer(Song song, bool printStartupDiagnostics)
    {
        var nextPlayer = new Player(song, printStartupDiagnostics);
        var nextCancellation = new CancellationTokenSource();
        nextPlayer.PlaybackStoppedUnexpectedly += OnPlaybackStoppedUnexpectedly;
        Task nextTask = nextPlayer.RunAsync(nextCancellation.Token);

        lock (playerLock)
        {
            currentPlayer = nextPlayer;
            currentPlaybackCancellation = nextCancellation;
            currentPlaybackTask = nextTask;
        }
    }

    private void OnPlaybackStoppedUnexpectedly(
        object? sender,
        AudioEngineStoppedEventArgs args)
    {
        playbackStopSignals.Writer.TryWrite(args);
    }

    private async Task StopCurrentPlayerAsync()
    {
        Player? player;
        CancellationTokenSource? cancellation;
        Task? task;

        lock (playerLock)
        {
            player = currentPlayer;
            cancellation = currentPlaybackCancellation;
            task = currentPlaybackTask;

            currentPlayer = null;
            currentPlaybackCancellation = null;
            currentPlaybackTask = null;
        }

        if (player is not null)
        {
            player.PlaybackStoppedUnexpectedly -= OnPlaybackStoppedUnexpectedly;
        }

        cancellation?.Cancel();

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellation?.Dispose();
        player?.Dispose();
    }

    private static ConsoleKey MapKey(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key >= ConsoleKey.D1 && keyInfo.Key <= ConsoleKey.D9)
        {
            return ConsoleKey.F1 + (keyInfo.Key - ConsoleKey.D1);
        }

        return keyInfo.Key;
    }

    private Task ReadKeyboardAsync(CancellationTokenSource cancellation)
    {
        return Task.Factory.StartNew(
            () => ReadKeyboardLoop(cancellation),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void ReadKeyboardLoop(CancellationTokenSource cancellation)
    {
        Thread.CurrentThread.Name ??= "EDMAC Keyboard";
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

        while (!cancellation.IsCancellationRequested)
        {
            ConsoleKey key = MapKey(Console.ReadKey(intercept: true));
            if (key is ConsoleKey.Escape or ConsoleKey.Q)
            {
                return;
            }

            try
            {
                HandleKeyboardInputAsync(key).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ConsoleUi.Error($"Key handling failed: {exception.Message}");
            }
        }
    }

    private async Task HandleKeyboardInputAsync(ConsoleKey key)
    {
        if (key == ConsoleKey.Spacebar)
        {
            await TogglePlayPauseAsync();
            return;
        }

        if (key == ConsoleKey.H)
        {
            await RestartAsync();
            return;
        }

        if (key == ConsoleKey.R)
        {
            ToggleRecording();
            return;
        }

        Player? player;
        lock (playerLock)
        {
            player = currentPlayer;
        }

        player?.HandleKey(key);
    }

    private void HandleMidiControl(ConsoleKey key)
    {
        try
        {
            Player? player;
            lock (playerLock)
            {
                player = currentPlayer;
            }

            player?.HandleKey(key);
        }
        catch (Exception exception)
        {
            ConsoleUi.Error($"MIDI control failed: {exception.Message}");
        }
    }

    private void HandleMidiPlayPause()
    {
        try
        {
            TogglePlayPauseAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            ConsoleUi.Error($"MIDI play/pause failed: {exception.Message}");
        }
    }

    private async Task TogglePlayPauseAsync()
    {
        await transportLock.WaitAsync();
        try
        {
            if (paused)
            {
                if (currentSong is null)
                {
                    ConsoleUi.Warning("No song is loaded.");
                    return;
                }

                if (HasCurrentPlayer())
                {
                    await StopCurrentPlayerAsync();
                }

                StartPlayer(currentSong, printStartupDiagnostics: false);
                paused = false;
                ConsoleUi.Control("Play");
                return;
            }

            await StopCurrentPlayerAsync();
            paused = true;
            ConsoleUi.Control("Pause");
        }
        finally
        {
            transportLock.Release();
        }
    }

    private bool HasCurrentPlayer()
    {
        lock (playerLock)
        {
            return currentPlayer is not null;
        }
    }

    private async Task RestartAsync()
    {
        await transportLock.WaitAsync();
        try
        {
            if (currentSong is null)
            {
                ConsoleUi.Warning("No song is loaded.");
                return;
            }

            await StopCurrentPlayerAsync();
            StartPlayer(currentSong, printStartupDiagnostics: false);
            paused = false;
            ConsoleUi.Control("Restart");
        }
        finally
        {
            transportLock.Release();
        }
    }

    private void ToggleRecording()
    {
        Player? player;
        lock (playerLock)
        {
            player = currentPlayer;
        }

        if (player is null)
        {
            ConsoleUi.Warning("Start playback before recording.");
            return;
        }

        string recordingsDirectory = Path.Combine(AppContext.BaseDirectory, "recordings");
        (bool isRecording, string? path) = player.ToggleRecording(recordingsDirectory);

        if (isRecording)
        {
            ConsoleUi.Control($"Record on -> {path}");
            return;
        }

        ConsoleUi.Control(path is null ? "Record off" : $"Record off -> {path}");
    }

    private static void PrintControlSummary(Song song)
    {
        song.InitializeTrackEnabledStates();

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
            ConsoleUi.KeyValue(control.ToString(), trackNames);
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
}
