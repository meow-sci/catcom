namespace Catcom.Core.Runtime;

/// <summary>
///     An error raised while running script code. The only exception a runtime lets escape from
///     <c>Invoke</c>/<c>Advance</c> for a script failure.
/// </summary>
public sealed class ScriptRuntimeException : Exception
{
    /// <summary>Creates a runtime exception with no traceback.</summary>
    /// <param name="message">The error message.</param>
    public ScriptRuntimeException(string message)
        : base(message)
    {
        Traceback = "";
    }

    /// <summary>Creates a runtime exception with a script traceback and location.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="traceback">The script-level traceback text.</param>
    /// <param name="file">The script file the error was raised in, if known.</param>
    /// <param name="line">The line the error was raised on, if known.</param>
    /// <param name="inner">The underlying exception, if any.</param>
    public ScriptRuntimeException(string message, string traceback, string? file = null, int? line = null, Exception? inner = null)
        : base(message, inner)
    {
        Traceback = traceback;
        File = file;
        Line = line;
    }

    /// <summary>The script-level traceback text (empty when none).</summary>
    public string Traceback { get; }

    /// <summary>The script file the error was raised in, if known.</summary>
    public string? File { get; }

    /// <summary>The line the error was raised on, if known.</summary>
    public int? Line { get; }
}
