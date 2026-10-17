namespace Catcom.Core.Commands;

/// <summary>
///     The outcome of executing a <see cref="SimCommand"/>, mapped 1:1 to the frozen control errno
///     vocabulary (see <see cref="CommandOutcomes.ErrnoName"/>).
/// </summary>
public enum CommandOutcome
{
    /// <summary>The mutation applied successfully.</summary>
    Ok,

    /// <summary>Unparseable or out-of-range argument (EINVAL).</summary>
    Invalid,

    /// <summary>The vessel/module vanished between walk and execution (ENOENT).</summary>
    NotFound,

    /// <summary>Control disabled by config — authority or debug gating (EACCES).</summary>
    Denied,

    /// <summary>The action cannot fire right now, e.g. an already-fired one-shot (EBUSY).</summary>
    Busy,

    /// <summary>A KSA API threw — integration-layer fault, flips the accessor health latch (EIO).</summary>
    Fault,

    /// <summary>The game thread did not drain the command in time — paused/loading (ETIMEDOUT).</summary>
    TimedOut,

    /// <summary>The accessor is disabled by its health latch after a prior fault (EOPNOTSUPP).</summary>
    Unsupported,
}

/// <summary>The result of submitting a command: an <see cref="CommandOutcome"/> and an optional message.</summary>
/// <param name="Outcome">The classified outcome.</param>
/// <param name="Message">A human-readable detail for logs and script errors.</param>
public sealed record CommandResult(CommandOutcome Outcome, string? Message = null)
{
    /// <summary>The shared success result.</summary>
    public static CommandResult Ok { get; } = new(CommandOutcome.Ok);

    /// <summary>Whether the command applied successfully.</summary>
    public bool IsSuccess => Outcome == CommandOutcome.Ok;

    /// <summary>An <see cref="CommandOutcome.Invalid"/> result (unparseable or out-of-range argument).</summary>
    /// <param name="message">The detail message.</param>
    /// <returns>The result.</returns>
    public static CommandResult Invalid(string message) => new(CommandOutcome.Invalid, message);

    /// <summary>A <see cref="CommandOutcome.NotFound"/> result (the vessel/module vanished).</summary>
    /// <param name="message">The detail message.</param>
    /// <returns>The result.</returns>
    public static CommandResult NotFound(string message) => new(CommandOutcome.NotFound, message);

    /// <summary>A <see cref="CommandOutcome.Denied"/> result (control or debug gating).</summary>
    /// <param name="message">The detail message.</param>
    /// <returns>The result.</returns>
    public static CommandResult Denied(string message) => new(CommandOutcome.Denied, message);

    /// <summary>A <see cref="CommandOutcome.Busy"/> result (the action cannot fire right now).</summary>
    /// <param name="message">The detail message.</param>
    /// <returns>The result.</returns>
    public static CommandResult Busy(string message) => new(CommandOutcome.Busy, message);

    /// <summary>A <see cref="CommandOutcome.Fault"/> result (a game API threw).</summary>
    /// <param name="message">The detail message.</param>
    /// <returns>The result.</returns>
    public static CommandResult Fault(string message) => new(CommandOutcome.Fault, message);

    /// <summary>A <see cref="CommandOutcome.TimedOut"/> result (the game thread did not drain in time).</summary>
    /// <param name="message">The detail message.</param>
    /// <returns>The result.</returns>
    public static CommandResult TimedOut(string message) => new(CommandOutcome.TimedOut, message);

    /// <summary>An <see cref="CommandOutcome.Unsupported"/> result (the accessor is disabled or the action is unavailable).</summary>
    /// <param name="message">The detail message.</param>
    /// <returns>The result.</returns>
    public static CommandResult Unsupported(string message) => new(CommandOutcome.Unsupported, message);
}

/// <summary>
///     The single source of truth for turning a <see cref="CommandOutcome"/> into the canonical errno
///     name used in script-visible results and logs.
/// </summary>
public static class CommandOutcomes
{
    /// <summary>The canonical errno name (<c>"EINVAL"</c>, …) for text/JSON consumers; <c>"OK"</c> for success.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The errno name.</returns>
    public static string ErrnoName(this CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Ok => "OK",
        CommandOutcome.Invalid => "EINVAL",
        CommandOutcome.NotFound => "ENOENT",
        CommandOutcome.Denied => "EACCES",
        CommandOutcome.Busy => "EBUSY",
        CommandOutcome.TimedOut => "ETIMEDOUT",
        CommandOutcome.Unsupported => "EOPNOTSUPP",
        _ => "EIO",
    };
}
