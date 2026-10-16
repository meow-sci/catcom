using KsaScripting.Core.Commands;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Hosting;

/// <summary>
///     The game side of the host: implemented by <c>KsaScripting.Ksa.KsaBridge</c>. Game thread only.
///     Nothing here may throw on the sim path; failures are returned as <see cref="CommandResult"/> or
///     <see cref="ScriptValue.Null"/>.
/// </summary>
public interface IGameBridge
{
    /// <summary>Reads a value from a game object.</summary>
    /// <param name="handle"><c>"vessel:&lt;id&gt;"</c> | <c>"time"</c> | <c>"body:&lt;id&gt;"</c> | <c>"system"</c>.</param>
    /// <param name="path">A dotted path (see <see cref="SnapshotPath"/>); empty for the whole record.</param>
    /// <returns>The value, or <see cref="ScriptValue.Null"/> when absent.</returns>
    ScriptValue Read(string handle, string path);

    /// <summary>Reads a whole record as a map.</summary>
    /// <param name="handle">The handle to snapshot.</param>
    /// <returns>The record as a map, or <see cref="ScriptValue.Null"/> when unknown.</returns>
    ScriptValue Snapshot(string handle);

    /// <summary>Lists the handles of a kind.</summary>
    /// <param name="kind"><c>"vessels"</c> | <c>"bodies"</c>.</param>
    /// <returns>The handles.</returns>
    IReadOnlyList<string> List(string kind);

    /// <summary>Executes a Frame-phase command immediately. Never throws.</summary>
    /// <param name="command">The command.</param>
    /// <returns>The outcome.</returns>
    CommandResult Execute(SimCommand command);

    /// <summary>Queues a Solver-phase command; the result is delivered in a later Step slot.</summary>
    /// <param name="command">The command.</param>
    /// <param name="onResult">Invoked on the game thread with the outcome.</param>
    void QueueSolver(SimCommand command, Action<CommandResult> onResult);

    /// <summary>The universal time, in seconds.</summary>
    double UtSeconds { get; }

    /// <summary>The duration of the last simulation step, in seconds.</summary>
    double SimDt { get; }

    /// <summary>The current time-warp factor.</summary>
    double Warp { get; }

    /// <summary>Whether the simulation is paused.</summary>
    bool Paused { get; }

    /// <summary>The render frame number.</summary>
    ulong FrameNumber { get; }

    /// <summary>The catalog of events the bridge can produce.</summary>
    IReadOnlyList<EventDescriptor> EventCatalog { get; }

    /// <summary>Executes a game console command.</summary>
    /// <param name="command">The console command line.</param>
    void ConsoleExec(string command);
}
