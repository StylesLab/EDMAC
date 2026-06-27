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

            if (args.Length >= 2 &&
                args[0].Equals("render", StringComparison.OrdinalIgnoreCase))
            {
                RenderOptions options = ParseRenderOptions(args);
                Song song = SongLoader.Load(options.SongPath, AudioEngine.DefaultSampleRate);

                using var renderPlayer = new Player(song, printStartupDiagnostics: false);
                renderPlayer.RenderToFile(options.OutputPath, options.Seconds);
                ConsoleUi.Success($"Rendered {options.Seconds:0.###}s to {Path.GetFullPath(options.OutputPath)}");
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
        Console.WriteLine("  edmac validate song.yaml");
        Console.WriteLine("  edmac render song.yaml --seconds 32 --out take.wav");
    }

    private static RenderOptions ParseRenderOptions(string[] args)
    {
        string songPath = args[1];
        double? seconds = null;
        string? outputPath = null;

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

            throw new ArgumentException($"Unknown render option '{argument}'.");
        }

        if (seconds is null)
        {
            throw new ArgumentException("Render requires --seconds.");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Render requires --out.");
        }

        return new RenderOptions(songPath, seconds.Value, outputPath);
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

    private sealed record RenderOptions(
        string SongPath,
        double Seconds,
        string OutputPath);
}
