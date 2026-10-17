using Catcom.Core.Hooks;
using Catcom.Core.Logging;

namespace Catcom.Core.Terminals;

/// <summary>
///     Owns every <see cref="ScriptTerminal"/>: one per script, ids starting at 1. Each terminal's
///     events are forwarded to the <see cref="EventBus"/> (targeted at the owning script). Game-thread
///     only.
/// </summary>
public sealed class TerminalRegistry
{
    private readonly int _bufferKiB;
    private readonly EventBus? _events;
    private readonly SortedDictionary<int, ScriptTerminal> _terminals = [];
    private readonly Dictionary<int, Action<ScriptEvent>> _eventHandlers = [];
    private readonly Dictionary<int, Action<ScriptTerminal>> _detachHandlers = [];
    private int _nextId = 1;

    /// <summary>Creates the registry.</summary>
    /// <param name="bufferKiB">The output ring buffer size of every terminal, in KiB.</param>
    /// <param name="events">Where terminal events are published, or <see langword="null"/> to forward nothing.</param>
    public TerminalRegistry(int bufferKiB = 256, EventBus? events = null)
    {
        _bufferKiB = Math.Max(1, bufferKiB);
        _events = events;
    }

    /// <summary>Raised when any terminal lost its last surface while its <see cref="ScriptTerminal.StopWhenDetached"/> is set.</summary>
    public event Action<ScriptTerminal>? LastDetached;

    /// <summary>Creates the terminal of a script, or returns the one it already has.</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <returns>The script's terminal.</returns>
    public ScriptTerminal Create(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var existing = ForScript(script);
        if (existing is not null)
            return existing;

        var terminal = new ScriptTerminal(_nextId++, script, _bufferKiB);
        Action<ScriptEvent> onEvent = ev => Forward(ev);
        Action<ScriptTerminal> onDetached = t => RaiseLastDetached(t);
        terminal.Event += onEvent;
        terminal.LastDetached += onDetached;
        _terminals[terminal.Id] = terminal;
        _eventHandlers[terminal.Id] = onEvent;
        _detachHandlers[terminal.Id] = onDetached;
        return terminal;
    }

    /// <summary>Looks a terminal up by id.</summary>
    /// <param name="id">The terminal id.</param>
    /// <returns>The terminal, or <see langword="null"/>.</returns>
    public ScriptTerminal? Get(int id) => _terminals.GetValueOrDefault(id);

    /// <summary>Looks a terminal up by its owning script.</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <returns>The terminal, or <see langword="null"/>.</returns>
    public ScriptTerminal? ForScript(string script)
        => _terminals.Values.FirstOrDefault(t => string.Equals(t.Script, script, StringComparison.Ordinal));

    /// <summary>Forgets a terminal (it is not terminated; call <see cref="ScriptTerminal.Terminate"/> first to notify its surfaces).</summary>
    /// <param name="id">The terminal id.</param>
    public void Remove(int id)
    {
        if (!_terminals.Remove(id, out var terminal))
            return;

        if (_eventHandlers.Remove(id, out var onEvent))
            terminal.Event -= onEvent;
        if (_detachHandlers.Remove(id, out var onDetached))
            terminal.LastDetached -= onDetached;
    }

    /// <summary>Every terminal, ordered by id.</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<ScriptTerminal> All() => _terminals.Values.ToArray();

    /// <summary>Flushes the pending output of every terminal (the last step of a Step slot).</summary>
    public void FlushAll()
    {
        foreach (var terminal in _terminals.Values.ToArray())
        {
            try
            {
                terminal.Flush();
            }
            catch (Exception ex)
            {
                Log.LogOnce($"terminal.flush:{terminal.Script}", LogLevel.Warn, $"Flushing the terminal of '{terminal.Script}' failed: {ex.Message}");
            }
        }
    }

    private void Forward(ScriptEvent ev)
    {
        try
        {
            _events?.Publish(ev);
        }
        catch (Exception ex)
        {
            Log.LogOnce("terminal.forward", LogLevel.Warn, $"Publishing a terminal event failed: {ex.Message}");
        }
    }

    private void RaiseLastDetached(ScriptTerminal terminal)
    {
        try
        {
            LastDetached?.Invoke(terminal);
        }
        catch (Exception ex)
        {
            Log.LogOnce("terminal.lastdetached", LogLevel.Warn, $"A terminal LastDetached handler failed: {ex.Message}");
        }
    }
}
