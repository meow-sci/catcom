using System.Collections.Concurrent;
using KsaScripting.Core.Values;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace KsaScripting.Runtime.V8;

/// <summary>Everything the runtime owns for one loaded script: its isolate, engine, loader, queue context, values and callables.</summary>
internal sealed class LoadedScript
{
    private int _disposed;

    /// <summary>Creates the state for a script that is being loaded.</summary>
    /// <param name="token">The module token.</param>
    /// <param name="name">The script name.</param>
    /// <param name="path">The script file path.</param>
    /// <param name="runtime">The isolate the engine lives in.</param>
    /// <param name="ownsRuntime">Whether the script owns <paramref name="runtime"/> (disposed with it) or only borrows the shared debuggable one.</param>
    /// <param name="engine">The script's engine.</param>
    /// <param name="context">The engine's synchronization context.</param>
    /// <param name="loader">The engine's module loader.</param>
    public LoadedScript(long token, string name, string path, V8Runtime runtime, bool ownsRuntime, V8ScriptEngine engine, GameThreadSynchronizationContext context, TsDocumentLoader loader)
    {
        Token = token;
        Name = name;
        Path = path;
        Runtime = runtime;
        OwnsRuntime = ownsRuntime;
        Engine = engine;
        Context = context;
        Loader = loader;
        context.Tag = this;
    }

    /// <summary>The module token handed out in <c>ScriptModule</c>.</summary>
    public long Token { get; }

    /// <summary>The script name.</summary>
    public string Name { get; }

    /// <summary>The script file path.</summary>
    public string Path { get; }

    /// <summary>The V8 isolate the script's engine lives in (its own, except in debug mode where all scripts share one).</summary>
    public V8Runtime Runtime { get; }

    /// <summary>Whether the script owns <see cref="Runtime"/> and disposes it on unload (always, except for the shared debuggable isolate).</summary>
    public bool OwnsRuntime { get; }

    /// <summary>The script's V8 engine (one per script).</summary>
    public V8ScriptEngine Engine { get; }

    /// <summary>The engine's game-thread synchronization context.</summary>
    public GameThreadSynchronizationContext Context { get; }

    /// <summary>The engine's module loader (also the owner of the source maps).</summary>
    public TsDocumentLoader Loader { get; }

    /// <summary>The per-engine value converter (set once the helper script is installed).</summary>
    public JsValues? Values { get; set; }

    /// <summary>The module namespace object of the entry module.</summary>
    public ScriptObject? Namespace { get; set; }

    /// <summary>The exported function names (sorted ordinally).</summary>
    public IReadOnlyList<string> ExportNames { get; set; } = [];

    /// <summary>The export callables handed out by <c>GetExport</c>.</summary>
    public Dictionary<string, ScriptCallable> ExportCallables { get; } = new(StringComparer.Ordinal);

    /// <summary>Every callable created for functions of this script (exports, hooks, predicates), by token.</summary>
    public ConcurrentDictionary<long, ScriptCallable> Callables { get; } = new();

    /// <summary>Promise rejections of this engine that nobody handled (yet).</summary>
    public RejectionTracker Rejections { get; } = new();

    /// <summary>Whether the script was unloaded.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Marks the script unloaded (returns <c>false</c> if it already was).</summary>
    public bool MarkDisposed() => Interlocked.Exchange(ref _disposed, 1) == 0;
}
