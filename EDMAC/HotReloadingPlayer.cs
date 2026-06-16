using System.Threading.Channels;

namespace EDMAC;

public sealed class HotReloadingPlayer
{
    private readonly string yamlPath;
    private readonly object playerLock = new();
    private readonly Channel<bool> reloadSignals;
    private Player? currentPlayer;
    private CancellationTokenSource? currentPlaybackCancellation;
    private Task? currentPlaybackTask;
    private bool hasLoadedOnce;

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
    }

    public async Task RunAsync()
    {
        Console.WriteLine("Press the configured function keys to toggle tracks. Press Escape to stop.");
        Console.WriteLine("Editing the YAML file will reload it and restart playback.");

        using var hostCancellation = new CancellationTokenSource();
        using FileSystemWatcher watcher = CreateWatcher();

        await ReloadAsync();
        watcher.EnableRaisingEvents = true;

        Task keyboardTask = ReadKeyboardAsync(hostCancellation);
        Task reloadTask = ProcessReloadsAsync(hostCancellation.Token);

        await keyboardTask;
        hostCancellation.Cancel();

        try
        {
            await reloadTask;
        }
        catch (OperationCanceledException)
        {
        }

        await StopCurrentPlayerAsync();
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

    private async Task ReloadAsync()
    {
        Song song;

        try
        {
            song = SongLoader.Load(yamlPath, AudioEngine.DefaultSampleRate);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Reload failed: {exception.Message}");
            return;
        }

        await StopCurrentPlayerAsync();

        try
        {
            var nextPlayer = new Player(song, printStartupDiagnostics: !hasLoadedOnce);
            var nextCancellation = new CancellationTokenSource();
            Task nextTask = nextPlayer.RunAsync(nextCancellation.Token);

            lock (playerLock)
            {
                currentPlayer = nextPlayer;
                currentPlaybackCancellation = nextCancellation;
                currentPlaybackTask = nextTask;
            }

            hasLoadedOnce = true;
            Console.WriteLine($"Reloaded {Path.GetFileName(yamlPath)}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Reload failed: {exception.Message}");
        }
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

            Player? player;
            lock (playerLock)
            {
                player = currentPlayer;
            }

            player?.HandleKey(key);
        }
    }
}
