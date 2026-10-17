using Catcom.Core.Hosting;
using Catcom.Core.Logging;
using Catcom.Core.Values;

namespace Catcom.Core.Hooks;

/// <summary>
///     The registry of every binding (code-declared and UI-created) and the callable behind each.
///     Game-thread only; every query returns a snapshot copy so callers may add or remove bindings
///     while iterating the result.
/// </summary>
public sealed class HookRegistry
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    /// <summary>
    ///     Resolves the vessel id meant by the <c>"active"</c> vessel filter at delivery time. Set by
    ///     the host facade; <see langword="null"/> means no active vessel is known.
    /// </summary>
    public Func<string?>? ActiveVesselProvider { get; set; }

    /// <summary>Raised after <see cref="Add"/>, <see cref="Remove"/>, <see cref="RemoveAll"/>, <see cref="RemoveWhere"/> and <see cref="SetEnabled"/> changed the registry.</summary>
    public event Action? Changed;

    /// <summary>Registers a binding. The registry takes ownership of <paramref name="fn"/> and disposes it on removal.</summary>
    /// <param name="script">The owning script's catalog name.</param>
    /// <param name="kind">What to attach to.</param>
    /// <param name="key">The trigger key (event name, tick kind, command name, ...).</param>
    /// <param name="fn">The function to call.</param>
    /// <param name="opts">Delivery options.</param>
    /// <param name="fromCode"><see langword="true"/> when declared by script code.</param>
    /// <returns>The new binding's id.</returns>
    public Guid Add(string script, BindingKind kind, string key, ScriptCallable fn, BindingOptions opts, bool fromCode)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fn);
        ArgumentNullException.ThrowIfNull(opts);

        var binding = new Binding(Guid.NewGuid(), script, fn.Name ?? "<anonymous>", kind, key, opts, fromCode, true);
        lock (_gate)
        {
            _entries.Add(new Entry(binding, fn));
        }

        RaiseChanged();
        return binding.Id;
    }

    /// <summary>Removes one binding and disposes its callable. Unknown ids are ignored.</summary>
    /// <param name="id">The binding id.</param>
    public void Remove(Guid id)
    {
        Entry? removed = null;
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Binding.Id == id);
            if (index >= 0)
            {
                removed = _entries[index];
                _entries.RemoveAt(index);
            }
        }

        if (removed is null)
            return;

        DisposeCallable(removed.Fn);
        RaiseChanged();
    }

    /// <summary>Removes every binding of a script and disposes their callables.</summary>
    /// <param name="script">The script's catalog name.</param>
    public void RemoveAll(string script) => RemoveWhere(script, _ => true);

    /// <summary>Removes the script's bindings that satisfy <paramref name="predicate"/> (used by the catalog's atomic swap on reload).</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <param name="predicate">Selects the bindings to remove.</param>
    public void RemoveWhere(string script, Func<Binding, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(predicate);

        List<Entry> removed = [];
        lock (_gate)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (!string.Equals(entry.Binding.Script, script, StringComparison.Ordinal) || !predicate(entry.Binding))
                    continue;

                removed.Add(entry);
                _entries.RemoveAt(i);
            }
        }

        if (removed.Count == 0)
            return;

        foreach (var entry in removed)
            DisposeCallable(entry.Fn);

        RaiseChanged();
    }

    /// <summary>Enables or disables a binding (the UI toggle).</summary>
    /// <param name="id">The binding id.</param>
    /// <param name="enabled">The new state.</param>
    /// <returns><see langword="false"/> when the id is unknown.</returns>
    public bool SetEnabled(Guid id, bool enabled)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Binding.Id == id);
            if (index < 0)
                return false;

            var entry = _entries[index];
            if (entry.Binding.Enabled != enabled)
                _entries[index] = entry with { Binding = entry.Binding with { Enabled = enabled } };
        }

        RaiseChanged();
        return true;
    }

    /// <summary>The enabled event bindings that match <paramref name="type"/>, the vessel filter and the sync flag.</summary>
    /// <param name="type">The event type (exact, ordinal match against the binding key).</param>
    /// <param name="vesselId">The event's vessel id, if any.</param>
    /// <param name="sync"><see langword="true"/> for sync subscriptions, <see langword="false"/> for async ones.</param>
    /// <returns>A snapshot in registration order.</returns>
    public IReadOnlyList<(Binding Binding, ScriptCallable Fn)> ForEvent(string type, string? vesselId, bool sync)
    {
        var active = ResolveActiveVessel();
        return Query(b => b.Kind == BindingKind.Event
                          && b.Options.Sync == sync
                          && string.Equals(b.Key, type, StringComparison.Ordinal)
                          && VesselMatches(b.Options.Vessel, vesselId, active));
    }

    /// <summary>The enabled tick bindings of one tick kind.</summary>
    /// <param name="key"><c>frame</c>, <c>step</c> or <c>every</c>.</param>
    /// <returns>A snapshot in registration order.</returns>
    public IReadOnlyList<(Binding Binding, ScriptCallable Fn)> ForTick(string key)
        => Query(b => b.Kind == BindingKind.Tick && string.Equals(b.Key, key, StringComparison.Ordinal));

    /// <summary>The enabled service bindings.</summary>
    /// <returns>A snapshot in registration order.</returns>
    public IReadOnlyList<(Binding Binding, ScriptCallable Fn)> Services()
        => Query(b => b.Kind == BindingKind.Service);

    /// <summary>The enabled command bindings for a command name (ignoring case).</summary>
    /// <param name="name">The command name.</param>
    /// <returns>A snapshot in registration order.</returns>
    public IReadOnlyList<(Binding Binding, ScriptCallable Fn)> Commands(string name)
        => Query(b => b.Kind == BindingKind.Command && string.Equals(b.Key, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every binding, enabled or not.</summary>
    /// <returns>A snapshot in registration order.</returns>
    public IReadOnlyList<Binding> All()
    {
        lock (_gate)
        {
            return _entries.Select(e => e.Binding).ToArray();
        }
    }

    /// <summary>Every binding of one script, enabled or not.</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <returns>A snapshot in registration order.</returns>
    public IReadOnlyList<Binding> ForScript(string script)
    {
        lock (_gate)
        {
            return _entries
                .Where(e => string.Equals(e.Binding.Script, script, StringComparison.Ordinal))
                .Select(e => e.Binding)
                .ToArray();
        }
    }

    /// <summary>
    ///     The vessel-filter rule shared by event bindings and <c>waitEvent</c> waits: a null, empty or
    ///     <c>any</c> filter matches everything; <c>active</c> matches when the event's vessel is the
    ///     active vessel; anything else must equal the event's vessel id.
    /// </summary>
    /// <param name="filter">The filter text.</param>
    /// <param name="eventVesselId">The event's vessel id.</param>
    /// <returns><see langword="true"/> when the event passes the filter.</returns>
    internal bool MatchesVessel(string? filter, string? eventVesselId)
        => VesselMatches(filter, eventVesselId, ResolveActiveVessel());

    private static bool VesselMatches(string? filter, string? eventVesselId, string? activeVesselId)
    {
        if (string.IsNullOrEmpty(filter) || string.Equals(filter, "any", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(filter, "active", StringComparison.OrdinalIgnoreCase))
            return eventVesselId is not null && activeVesselId is not null
                                             && string.Equals(eventVesselId, activeVesselId, StringComparison.Ordinal);

        return string.Equals(filter, eventVesselId, StringComparison.Ordinal);
    }

    private string? ResolveActiveVessel()
    {
        try
        {
            return ActiveVesselProvider?.Invoke();
        }
        catch (Exception ex)
        {
            Log.LogOnce("hooks.active-vessel", LogLevel.Warn, $"Resolving the active vessel for hook filters failed: {ex.Message}");
            return null;
        }
    }

    private IReadOnlyList<(Binding Binding, ScriptCallable Fn)> Query(Func<Binding, bool> predicate)
    {
        lock (_gate)
        {
            var result = new List<(Binding, ScriptCallable)>();
            foreach (var entry in _entries)
            {
                if (entry.Binding.Enabled && !entry.Fn.IsDisposed && predicate(entry.Binding))
                    result.Add((entry.Binding, entry.Fn));
            }

            return result;
        }
    }

    private static void DisposeCallable(ScriptCallable fn)
    {
        try
        {
            fn.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogOnce("hooks.dispose", LogLevel.Warn, $"Disposing a hook callable failed: {ex.Message}");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.LogOnce("hooks.changed", LogLevel.Warn, $"A HookRegistry.Changed handler failed: {ex.Message}");
        }
    }

    private sealed record Entry(Binding Binding, ScriptCallable Fn);
}
