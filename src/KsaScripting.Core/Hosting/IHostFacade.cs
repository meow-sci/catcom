using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;
using KsaScripting.Core.Commands;

namespace KsaScripting.Core.Hosting;

/// <summary>
///     What runtimes bind: the narrow, script-facing surface of the host. Implemented by
///     <c>HostFacade</c> (Core) over the game bridge, scheduler, hook registry, terminals and state store.
///     All members are called on the game thread.
/// </summary>
public interface IHostFacade
{
    // ---------------------------------------------------------------- reads

    /// <summary>Reads a value from a host object.</summary>
    /// <param name="handle"><c>"vessel:&lt;id&gt;"</c> | <c>"time"</c> | <c>"body:&lt;id&gt;"</c> | <c>"system"</c>.</param>
    /// <param name="path">A dotted path such as <c>"position.cci"</c>, <c>"ut"</c>, <c>"mass"</c>, <c>"home"</c>; empty for the whole record.</param>
    /// <returns>The value, or <see cref="ScriptValue.Null"/> when absent.</returns>
    ScriptValue Read(string handle, string path);

    /// <summary>Reads a whole record as a map (vessel, body, time, system, <c>term:&lt;n&gt;</c>).</summary>
    /// <param name="handle">The handle to snapshot.</param>
    /// <returns>The record as a map, or <see cref="ScriptValue.Null"/> when the handle is unknown.</returns>
    ScriptValue Snapshot(string handle);

    /// <summary>Lists host objects.</summary>
    /// <param name="kind"><c>"vessels"</c> | <c>"bodies"</c> (lists of handles), <c>"events"</c> (event catalog maps), <c>"exports"</c>, …</param>
    /// <returns>A list value.</returns>
    ScriptValue List(string kind);

    // ---------------------------------------------------------------- writes

    /// <summary>Performs a game action using the gatOS action vocabulary (see <see cref="SimActions"/>).</summary>
    /// <param name="action">The action key, e.g. <c>"vessel.throttle"</c>.</param>
    /// <param name="vesselId">The target vessel id, or <c>null</c> for the controlled vessel / no vessel.</param>
    /// <param name="ordinal">The module ordinal, or <see cref="SimCommand.NoOrdinal"/>.</param>
    /// <param name="value">The numeric argument.</param>
    /// <param name="values">The vector payload, or <c>null</c>.</param>
    /// <param name="token">The symbolic payload, or <c>null</c>.</param>
    /// <param name="aux">The secondary symbolic payload, or <c>null</c>.</param>
    /// <returns>An immediate result (Frame-phase actions) or a pending wait (Solver-phase actions).</returns>
    ActResult Act(string action, string? vesselId, int ordinal, double value, double[]? values, string? token, string? aux);

    // ---------------------------------------------------------------- waits

    /// <summary>Creates a wait the scheduler resolves on the game thread in the Step slot.</summary>
    /// <param name="kind">
    ///     <c>"sleep"</c>(seconds, clock) | <c>"nextStep"</c> | <c>"nextFrame"</c> | <c>"until"</c>(fn) |
    ///     <c>"waitEvent"</c>(name, vessel) | <c>"termRead"</c>(termId).
    /// </param>
    /// <param name="arg">The kind-specific argument.</param>
    /// <returns>The pending wait.</returns>
    HostWait Wait(string kind, ScriptValue arg);

    // ---------------------------------------------------------------- hooks

    /// <summary>Registers a script callback.</summary>
    /// <param name="kind"><c>"event"</c> | <c>"tick"</c> | <c>"service"</c> | <c>"command"</c>.</param>
    /// <param name="key">The event name / tick key / command name.</param>
    /// <param name="fn">The callback.</param>
    /// <param name="opts">Subscription options.</param>
    /// <returns>The subscription id.</returns>
    Guid Subscribe(string kind, string key, ScriptCallable fn, BindingOptions opts);

    /// <summary>Removes a subscription.</summary>
    /// <param name="id">The subscription id returned by <see cref="Subscribe"/>.</param>
    void Unsubscribe(Guid id);

    // ---------------------------------------------------------------- misc

    /// <summary>Writes a log line.</summary>
    /// <param name="level">0 debug, 1 info, 2 warn, 3 error.</param>
    /// <param name="message">The message.</param>
    void Log(int level, string message);

    /// <summary>Executes a game console command on the game thread.</summary>
    /// <param name="command">The console command line.</param>
    void ConsoleExec(string command);

    /// <summary>Reads a persisted per-script state value.</summary>
    /// <param name="key">The state key.</param>
    /// <returns>The value, or <see cref="ScriptValue.Null"/>.</returns>
    ScriptValue StateGet(string key);

    /// <summary>Writes a persisted per-script state value.</summary>
    /// <param name="key">The state key.</param>
    /// <param name="value">The value to store.</param>
    void StateSet(string key, ScriptValue value);

    // ---------------------------------------------------------------- terminals

    /// <summary>Writes bytes to a script terminal.</summary>
    /// <param name="termId">The terminal id.</param>
    /// <param name="bytes">The bytes (typically ANSI text).</param>
    void TermWrite(int termId, ReadOnlySpan<byte> bytes);

    /// <summary>Describes a terminal.</summary>
    /// <param name="termId">The terminal id.</param>
    /// <returns>A map <c>{cols, rows, attached}</c>.</returns>
    ScriptValue TermInfo(int termId);

    /// <summary>Sets a terminal's window title.</summary>
    /// <param name="termId">The terminal id.</param>
    /// <param name="title">The title.</param>
    void TermSetTitle(int termId, string title);

    /// <summary>Sets whether a terminal's script stops when the terminal is detached.</summary>
    /// <param name="termId">The terminal id.</param>
    /// <param name="stopWhenDetached">Whether to stop when detached.</param>
    void TermPolicy(int termId, bool stopWhenDetached);

    // ---------------------------------------------------------------- identity

    /// <summary>
    ///     The name of the script currently executing (set by <see cref="EnterScript"/>); <c>""</c>
    ///     (never <c>null</c>) when no script is entered.
    /// </summary>
    string CurrentScript { get; }

    /// <summary>Attributes subsequent host calls to <paramref name="script"/> until the returned scope is disposed (restores the previous value). The scheduler wraps every Invoke in it; runtimes wrap every continuation they resume inside Advance (promise jobs / coroutine resumes) so Subscribe/State/Wait/Log calls are attributed to the right script.</summary>
    /// <param name="script">The script name to attribute host calls to.</param>
    /// <returns>A scope that restores the previous <see cref="CurrentScript"/> when disposed.</returns>
    IDisposable EnterScript(string script);

    /// <summary>Reports a script error raised asynchronously (an unhandled promise rejection or a coroutine that errored inside Advance) for the current script (see EnterScript); counts toward the fault limit exactly like an Invoke failure.</summary>
    /// <param name="error">The error to report.</param>
    void ReportError(ScriptRuntimeException error);
}
