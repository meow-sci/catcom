using Brutal.ImGuiApi.Abstractions;
using KSA;
using KsaScripting.Core.Logging;

namespace KsaScripting.GameMod;

/// <summary>
///     The mod's log sink: every message goes to stdout (<see cref="Console.WriteLine(string)"/>) and, once
///     the game console exists, to the in-game console through the lock-guarded, thread-safe
///     <c>ConsoleWindow.Print</c> overloads (warnings and errors in the game's own warning / error
///     colours). A multi-line message (an exception trace) is printed one console line per text line, with
///     the prefix on the first. Never throws.
/// </summary>
public sealed class ModLogSink : ILogSink
{
    private const int MaxConsoleLines = 24;

    /// <inheritdoc />
    public void Write(LogLevel level, string message)
    {
        string text;
        try
        {
            text = Log.Format(level, message ?? "");
        }
        catch
        {
            return;
        }

        try
        {
            Console.WriteLine(text);
        }
        catch
        {
            // stdout may be closed; the in-game console is still worth trying.
        }

        try
        {
            PrintToGameConsole(level, text);
        }
        catch
        {
            // The ConsoleWindow may not exist yet (early start-up) or be shutting down.
        }
    }

    private static void PrintToGameConsole(LogLevel level, string text)
    {
        var console = Program.ConsoleWindow;
        if (console is null)
            return;

        var printed = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            if (printed == MaxConsoleLines)
            {
                console.Print("[ksa-scripting] ... (truncated, see the log)");
                return;
            }

            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            switch (level)
            {
                case LogLevel.Error:
                    console.Print(line.AsSpan(), ConsoleWindow.ErrorColor);
                    break;
                case LogLevel.Warn:
                    console.Print(line.AsSpan(), ConsoleWindow.WarningColor);
                    break;
                case LogLevel.Debug:
                    console.Print(line.AsSpan(), ConsoleWindow.DebugColor);
                    break;
                default:
                    console.Print(line);
                    break;
            }

            printed++;
        }
    }
}
