namespace EDMAC;

public static class ConsoleUi
{
    public static void Banner()
    {
        WriteLine("  ______ _____  __  __        _____ ", ConsoleColor.Cyan);
        WriteLine(" |  ____|  __ \\|  \\/  |      / ____|", ConsoleColor.Cyan);
        WriteLine(" | |__  | |  | | \\  / | __ _| |     ", ConsoleColor.DarkCyan);
        WriteLine(" |  __| | |  | | |\\/| |/ _` | |     ", ConsoleColor.Blue);
        WriteLine(" | |____| |__| | |  | | (_| | |____ ", ConsoleColor.DarkMagenta);
        WriteLine(" |______|_____/|_|  |_|\\__,_|\\_____|", ConsoleColor.Magenta);
        Console.WriteLine();
    }

    public static void Info(string message)
    {
        WritePrefixed("info", message, ConsoleColor.Cyan);
    }

    public static void Success(string message)
    {
        WritePrefixed("ok", message, ConsoleColor.Green);
    }

    public static void Warning(string message)
    {
        WritePrefixed("warn", message, ConsoleColor.Yellow);
    }

    public static void Error(string message)
    {
        WritePrefixed("error", message, ConsoleColor.Red, error: true);
    }

    public static void Control(string message)
    {
        WritePrefixed("ctrl", message, ConsoleColor.Magenta);
    }

    public static void Track(string name, string status)
    {
        WriteColored(name, ConsoleColor.Cyan);
        Console.Write(" ");
        WriteLine(status, status == "enabled" ? ConsoleColor.Green : ConsoleColor.DarkYellow);
    }

    public static void KeyValue(string key, string value)
    {
        WriteColored(key, ConsoleColor.DarkCyan);
        Console.Write("=");
        WriteLine(value, ConsoleColor.Gray);
    }

    private static void WritePrefixed(
        string prefix,
        string message,
        ConsoleColor color,
        bool error = false)
    {
        TextWriter writer = error ? Console.Error : Console.Out;

        if (Console.IsOutputRedirected || error && Console.IsErrorRedirected)
        {
            writer.WriteLine($"[{prefix}] {message}");
            return;
        }

        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        writer.Write($"[{prefix}] ");
        Console.ForegroundColor = previous;
        writer.WriteLine(message);
    }

    private static void WriteLine(string text, ConsoleColor color)
    {
        WriteColored(text, color);
        Console.WriteLine();
    }

    private static void WriteColored(string text, ConsoleColor color)
    {
        if (Console.IsOutputRedirected)
        {
            Console.Write(text);
            return;
        }

        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}
