using System.Text.Json;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Values;
using Tomlyn;

namespace KsaScripting.Core.Persistence;

/// <summary>
///     Per-script persistent key/value state (<c>ksa.state</c>). Values are stored as
///     <see cref="ScriptValue"/>s and persisted as their JSON text, so types (null excluded, bytes and
///     handles included) round-trip. Function values cannot persist and are rejected. Game-thread only.
/// </summary>
public sealed class ScriptStateStore
{
    private const int MaxDepth = 32;

    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly object _gate = new();
    private Dictionary<string, Dictionary<string, ScriptValue>> _scripts = new(StringComparer.Ordinal);

    /// <summary>Reads a value.</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <param name="key">The state key.</param>
    /// <returns>The value, or <see cref="ScriptValue.Null"/> when missing.</returns>
    public ScriptValue Get(string script, string key)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return _scripts.TryGetValue(script, out var keys) && keys.TryGetValue(key, out var value)
                ? value
                : ScriptValue.Null;
        }
    }

    /// <summary>Stores a value; <see cref="ScriptValue.Null"/> removes the key.</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <param name="key">The state key.</param>
    /// <param name="v">The value.</param>
    /// <exception cref="ArgumentException">The value is, or contains, a Function.</exception>
    public void Set(string script, string key, ScriptValue v)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(v);

        if (ContainsFunction(v, 0))
            throw new ArgumentException("functions cannot be stored in script state", nameof(v));

        lock (_gate)
        {
            if (v.IsNull)
            {
                if (_scripts.TryGetValue(script, out var existing) && existing.Remove(key) && existing.Count == 0)
                    _scripts.Remove(script);
                return;
            }

            if (!_scripts.TryGetValue(script, out var keys))
                _scripts[script] = keys = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
            keys[key] = v;
        }
    }

    /// <summary>All stored values of a script.</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <returns>A snapshot copy (empty when the script has no state).</returns>
    public IReadOnlyDictionary<string, ScriptValue> All(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        lock (_gate)
        {
            return _scripts.TryGetValue(script, out var keys)
                ? new Dictionary<string, ScriptValue>(keys, StringComparer.Ordinal)
                : new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
        }
    }

    /// <summary>Removes all state of a script.</summary>
    /// <param name="script">The script's catalog name.</param>
    public void Clear(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        lock (_gate)
        {
            _scripts.Remove(script);
        }
    }

    /// <summary>
    ///     Serializes everything as TOML: a <c>[scripts."&lt;name&gt;"]</c> table per script holding
    ///     <c>key = "&lt;json&gt;"</c> entries. Scripts without keys are omitted; ordering is ordinal by
    ///     script and key name, so the text is deterministic.
    /// </summary>
    /// <returns>The TOML text.</returns>
    public string SerializeToml()
    {
        var file = new StateFile();
        lock (_gate)
        {
            foreach (var script in _scripts.Keys.Order(StringComparer.Ordinal))
            {
                var keys = _scripts[script];
                if (keys.Count == 0)
                    continue;

                var table = new Dictionary<string, string>(keys.Count, StringComparer.Ordinal);
                foreach (var key in keys.Keys.Order(StringComparer.Ordinal))
                    table[key] = keys[key].ToJson();
                file.Scripts[script] = table;
            }
        }

        return file.Scripts.Count == 0 ? "" : TomlSerializer.Serialize(file, TomlOptions);
    }

    /// <summary>Replaces everything with the content of a TOML document. A malformed document is logged as a warning and the store is left unchanged.</summary>
    /// <param name="toml">Text produced by <see cref="SerializeToml"/>.</param>
    public void LoadToml(string toml)
    {
        ArgumentNullException.ThrowIfNull(toml);

        var loaded = new Dictionary<string, Dictionary<string, ScriptValue>>(StringComparer.Ordinal);
        try
        {
            var file = string.IsNullOrWhiteSpace(toml)
                ? new StateFile()
                : TomlSerializer.Deserialize<StateFile>(toml, TomlOptions) ?? new StateFile();
            foreach (var (script, table) in file.Scripts)
            {
                if (table is null || table.Count == 0)
                    continue;

                var keys = new Dictionary<string, ScriptValue>(table.Count, StringComparer.Ordinal);
                foreach (var (key, json) in table)
                {
                    var value = ScriptValue.FromJson(json);
                    if (!value.IsNull)
                        keys[key] = value;
                }

                if (keys.Count > 0)
                    loaded[script] = keys;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Script state could not be loaded ({ex.Message}); keeping the current state.");
            return;
        }

        lock (_gate)
        {
            _scripts = loaded;
        }
    }

    private static bool ContainsFunction(ScriptValue v, int depth)
    {
        if (depth > MaxDepth)
            return false;

        switch (v.Kind)
        {
            case ScriptValueKind.Function:
                return true;
            case ScriptValueKind.List:
                foreach (var item in v.AsList())
                {
                    if (ContainsFunction(item, depth + 1))
                        return true;
                }

                return false;
            case ScriptValueKind.Map:
                foreach (var item in v.AsMap().Values)
                {
                    if (ContainsFunction(item, depth + 1))
                        return true;
                }

                return false;
            default:
                return false;
        }
    }

    private sealed class StateFile
    {
        public Dictionary<string, Dictionary<string, string>> Scripts { get; set; } = new(StringComparer.Ordinal);
    }
}
