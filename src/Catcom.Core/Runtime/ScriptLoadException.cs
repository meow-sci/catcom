namespace Catcom.Core.Runtime;

/// <summary>A script failed to compile or evaluate its top level.</summary>
public sealed class ScriptLoadException : Exception
{
    /// <summary>Creates a load exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="file">The file the error is in.</param>
    /// <param name="line">The 1-based line, or 0 when unknown.</param>
    /// <param name="column">The 1-based column, or 0 when unknown.</param>
    /// <param name="snippet">The offending source line, or empty.</param>
    /// <param name="inner">The underlying exception, if any.</param>
    public ScriptLoadException(string message, string file, int line = 0, int column = 0, string snippet = "", Exception? inner = null)
        : base(message, inner)
    {
        File = file;
        Line = line;
        Column = column;
        Snippet = snippet;
    }

    /// <summary>The file the error is in.</summary>
    public string File { get; }

    /// <summary>The 1-based line, or 0 when unknown.</summary>
    public int Line { get; }

    /// <summary>The 1-based column, or 0 when unknown.</summary>
    public int Column { get; }

    /// <summary>The offending source line, or empty.</summary>
    public string Snippet { get; }
}
