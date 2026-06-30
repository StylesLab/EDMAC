using System.Globalization;

namespace EDMAC;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 &&
                args[0].Equals("validate", StringComparison.OrdinalIgnoreCase))
            {
                Song song = SongLoader.Load(args[1], AudioEngine.DefaultSampleRate);
                SongValidator.Print(song);
                return 0;
            }

            if (args.Length == 1 &&
                args[0].Equals("midi", StringComparison.OrdinalIgnoreCase))
            {
                RunMidiMonitor();
                return 0;
            }

            if (args.Length >= 2 &&
                args[0].Equals("render", StringComparison.OrdinalIgnoreCase))
            {
                RenderOptions options = ParseRenderOptions(args);
                Song song = SongLoader.Load(options.SongPath, AudioEngine.DefaultSampleRate);
                Func<Track, bool>? trackFilter = CreateTrackFilter(song, options);

                using var renderPlayer = new Player(song, printStartupDiagnostics: false);
                if (options.UseArrangement)
                {
                    renderPlayer.RenderArrangementToFile(options.OutputPath, trackFilter);
                    ConsoleUi.Success($"Rendered arrangement to {Path.GetFullPath(options.OutputPath)}");
                    return 0;
                }

                if (options.Control is not null && !song.HasTrackControl(options.Control.Value))
                {
                    throw new ArgumentException(
                        $"Render control '{options.Control.Value}' does not match any track control.");
                }

                double seconds = options.Seconds ?? GetDefaultRenderSeconds(song);
                renderPlayer.RenderToFile(
                    options.OutputPath,
                    seconds,
                    options.Control,
                    trackFilter);
                ConsoleUi.Success($"Rendered {seconds:0.###}s to {Path.GetFullPath(options.OutputPath)}");
                return 0;
            }

            if (args.Length != 1)
            {
                PrintUsage();
                return 1;
            }

            var livePlayer = new HotReloadingPlayer(args[0]);
            await livePlayer.RunAsync();
            return 0;
        }
        catch (Exception exception)
        {
            ConsoleUi.Error(exception.Message);
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  edmac song.yaml");
        Console.WriteLine("  edmac midi");
        Console.WriteLine("  edmac validate song.yaml");
        Console.WriteLine("  edmac render song.yaml --seconds 32 --out take.wav");
        Console.WriteLine("  edmac render song.yaml --control f4 --seconds 16 --out chorus.wav");
        Console.WriteLine("  edmac render song.yaml --arrangement --out full.wav");
    }

    private static void RunMidiMonitor()
    {
        Environment.SetEnvironmentVariable("EDMAC_MIDI_TRACE", "1");

        using MidiControlInput input = MidiControlInput.Start(
            control => ConsoleUi.Control($"Mapped={control}"),
            () => ConsoleUi.Control("Mapped=Play/Pause"),
            MidiInputBackend.All);

        if (input.DeviceNames.Count == 0)
        {
            ConsoleUi.Info("No MIDI input devices found.");
        }
        else
        {
            foreach (string deviceName in input.DeviceNames)
            {
                ConsoleUi.Success($"MIDI input: {deviceName}");
            }
        }

        foreach (string failedDeviceMessage in input.FailedDeviceMessages)
        {
            ConsoleUi.Warning($"MIDI input skipped: {failedDeviceMessage}");
        }

        ConsoleUi.Info("MIDI monitor running. Press Q or Escape to quit.");

        while (true)
        {
            ConsoleKey key = Console.ReadKey(intercept: true).Key;
            if (key is ConsoleKey.Q or ConsoleKey.Escape)
            {
                return;
            }
        }
    }

    private static RenderOptions ParseRenderOptions(string[] args)
    {
        string songPath = args[1];
        double? seconds = null;
        string? outputPath = null;
        ConsoleKey? control = null;
        bool useArrangement = false;
        IReadOnlySet<string> tracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 2; index < args.Length; index++)
        {
            string argument = args[index];

            if (argument.Equals("--seconds", StringComparison.OrdinalIgnoreCase))
            {
                seconds = ParseRequiredPositiveDouble(args, ref index, "--seconds");
                continue;
            }

            if (argument.Equals("--out", StringComparison.OrdinalIgnoreCase))
            {
                outputPath = ParseRequiredValue(args, ref index, "--out");
                continue;
            }

            if (argument.Equals("--control", StringComparison.OrdinalIgnoreCase))
            {
                control = ParseControl(ParseRequiredValue(args, ref index, "--control"));
                continue;
            }

            if (argument.Equals("--arrangement", StringComparison.OrdinalIgnoreCase))
            {
                useArrangement = true;
                continue;
            }

            if (argument.Equals("--tracks", StringComparison.OrdinalIgnoreCase))
            {
                tracks = ParseCsv(ParseRequiredValue(args, ref index, "--tracks"));
                continue;
            }

            if (argument.Equals("--group", StringComparison.OrdinalIgnoreCase))
            {
                groups = ParseCsv(ParseRequiredValue(args, ref index, "--group"));
                continue;
            }

            throw new ArgumentException($"Unknown render option '{argument}'.");
        }

        if (useArrangement && control is not null)
        {
            throw new ArgumentException("Render cannot use both --arrangement and --control.");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Render requires --out.");
        }

        return new RenderOptions(
            songPath,
            seconds,
            outputPath,
            control,
            useArrangement,
            tracks,
            groups);
    }

    private static double ParseRequiredPositiveDouble(
        string[] args,
        ref int index,
        string optionName)
    {
        string value = ParseRequiredValue(args, ref index, optionName);

        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed) ||
            parsed <= 0 ||
            double.IsInfinity(parsed) ||
            double.IsNaN(parsed))
        {
            throw new ArgumentException($"{optionName} must be a positive number.");
        }

        return parsed;
    }

    private static string ParseRequiredValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{optionName} requires a value.");
        }

        index++;
        return args[index];
    }

    private static ConsoleKey ParseControl(string value)
    {
        if (!Enum.TryParse(value, true, out ConsoleKey key) ||
            key < ConsoleKey.F1 ||
            key > ConsoleKey.F24)
        {
            throw new ArgumentException("--control must be a function key such as f1 or f2.");
        }

        return key;
    }

    private static IReadOnlySet<string> ParseCsv(string value)
    {
        return value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Func<Track, bool>? CreateTrackFilter(Song song, RenderOptions options)
    {
        if (options.Tracks.Count == 0 && options.Groups.Count == 0)
        {
            return null;
        }

        string[] missingTracks = options.Tracks
            .Where(name => !song.Tracks.Any(track => track.Name.Equals(
                name,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missingTracks.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown render track(s): {string.Join(",", missingTracks)}.");
        }

        string[] missingGroups = options.Groups
            .Where(group => !song.Tracks.Any(track => track.Groups.Contains(group)))
            .ToArray();
        if (missingGroups.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown render group(s): {string.Join(",", missingGroups)}.");
        }

        return track =>
            options.Tracks.Contains(track.Name) ||
            track.Groups.Any(options.Groups.Contains);
    }

    private static double GetDefaultRenderSeconds(Song song)
    {
        return 16 * 4.0 * 60.0 / song.Bpm;
    }

    private sealed record RenderOptions(
        string SongPath,
        double? Seconds,
        string OutputPath,
        ConsoleKey? Control,
        bool UseArrangement,
        IReadOnlySet<string> Tracks,
        IReadOnlySet<string> Groups);
}
