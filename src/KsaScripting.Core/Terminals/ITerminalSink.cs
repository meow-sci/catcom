namespace KsaScripting.Core.Terminals;

/// <summary>
///     A surface a <see cref="ScriptTerminal"/> draws on (a purrTTY window, tab or in-world quad).
///     Called on the game thread; a sink that throws is detached.
/// </summary>
public interface ITerminalSink
{
    /// <summary>Receives output bytes (escape sequences included).</summary>
    /// <param name="bytes">The bytes to display.</param>
    void Write(ReadOnlySpan<byte> bytes);

    /// <summary>The terminal ended; the surface should show the message and stop accepting input.</summary>
    /// <param name="message">A human-readable reason such as <c>[script exited]</c>.</param>
    void Terminate(string message);
}
