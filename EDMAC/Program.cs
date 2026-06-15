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

            if (args.Length != 1)
            {
                PrintUsage();
                return 1;
            }

            Song playableSong = SongLoader.Load(args[0], AudioEngine.DefaultSampleRate);
            using var player = new Player(playableSong);
            await player.RunAsync();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  edmac song.yaml");
        Console.WriteLine("  edmac validate song.yaml");
    }
}
