using Catcom.Core.Commands;

namespace Catcom.Core.Scheduling;

/// <summary>
///     Receives the outcome of a fire-and-forget <see cref="GameThreadQueue.Post"/>. The callback runs
///     <b>inline on the game thread</b> inside <see cref="GameThreadQueue.Drain"/>, so an implementation
///     must be trivial (record a field, bump a counter) and must never block or throw.
/// </summary>
public interface IPostObserver
{
    /// <summary>Reports one posted command's result.</summary>
    /// <param name="token">The caller-supplied correlation token.</param>
    /// <param name="result">The outcome, exactly as an awaiting submit would have received it.</param>
    void OnCommandResult(int token, CommandResult result);
}
