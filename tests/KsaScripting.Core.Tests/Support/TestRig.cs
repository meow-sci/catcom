using KsaScripting.Core.Catalog;
using KsaScripting.Core.Hooks;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Scheduling;
using KsaScripting.Core.Terminals;
using KsaScripting.Core.Persistence;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Tests.Support;

/// <summary>
///     The full host stack on a <see cref="NullRuntime"/>: bridge, hooks, event bus, watchdog, scheduler,
///     terminals, state, facade, binding store and catalog over a temp scripts folder, with an injectable wall
///     clock. Budgets default to generous values so timing never makes a test flaky.
/// </summary>
internal sealed class TestRig : IDisposable
{
    public static readonly ScheduleConfig GenerousBudgets =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 3);

    private ulong _frame;
    private long _stepIndex;

    public TestRig(ScheduleConfig? config = null, HostFacadeOptions? options = null, string language = ScriptLanguages.Lua)
    {
        ScriptsDir = Path.Combine(Path.GetTempPath(), "ksa-scripting-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ScriptsDir);

        Bridge = new FakeGameBridge();
        Runtime = new NullRuntime("null", language);
        Runtimes = new RuntimeSet([Runtime]);
        Hooks = new HookRegistry();
        Events = new EventBus();
        Watchdog = new Watchdog();
        Terminals = new TerminalRegistry(256, Events);
        State = new ScriptStateStore();
        Scheduler = new Scheduler(config ?? GenerousBudgets, Hooks, Events, Runtimes, Watchdog, OnScriptError)
        {
            WallClock = () => Wall,
        };
        Scheduler.ScriptDisabled += (script, reason) => Disabled.Add((script, reason));
        Host = new HostFacade(Bridge, Scheduler, Hooks, Terminals, State, options);
        Runtime.Initialize(Host, new RuntimeOptions(ScriptsDir, Path.Combine(ScriptsDir, "stubs"), 0, false, null));
        Bindings = new BindingStore(Path.Combine(ScriptsDir, "bindings.toml"));
        Catalog = new ScriptCatalog(ScriptsDir, Runtimes, Hooks, Bindings, Scheduler, Host) { Terminals = Terminals };
    }

    public string ScriptsDir { get; }

    public FakeGameBridge Bridge { get; }

    public NullRuntime Runtime { get; }

    public RuntimeSet Runtimes { get; }

    public HookRegistry Hooks { get; }

    public EventBus Events { get; }

    public Watchdog Watchdog { get; }

    public TerminalRegistry Terminals { get; }

    public ScriptStateStore State { get; }

    public Scheduler Scheduler { get; }

    public HostFacade Host { get; }

    public BindingStore Bindings { get; }

    public ScriptCatalog Catalog { get; }

    /// <summary>The injected wall clock, in seconds.</summary>
    public double Wall { get; set; }

    /// <summary>Script errors reported through the scheduler's callback (also recorded in the facade's output ring).</summary>
    public List<(string Script, Exception Error)> Errors { get; } = [];

    /// <summary>(script, reason) of every <see cref="Scheduler.ScriptDisabled"/>.</summary>
    public List<(string Script, string Reason)> Disabled { get; } = [];

    /// <summary>The last step slot's info.</summary>
    public StepInfo? LastStep { get; private set; }

    /// <summary>Builds the next step (frame and step index advance automatically).</summary>
    public StepInfo Step(double ut, bool landed = true, double playerDt = 0.016, double dt = 0.02)
    {
        _frame++;
        if (landed)
            _stepIndex++;
        return new StepInfo(ut, dt, _stepIndex, _frame, landed, playerDt);
    }

    /// <summary>Runs one Step slot.</summary>
    public StepInfo Slot(double ut, bool landed = true, double playerDt = 0.016, double dt = 0.02)
    {
        var step = Step(ut, landed, playerDt, dt);
        LastStep = step;
        Scheduler.StepSlot(step);
        return step;
    }

    /// <summary>Writes a script file (parent folders are created) and returns its full path.</summary>
    public string WriteScript(string name, string code = "-- script\n")
    {
        var path = Path.Combine(ScriptsDir, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, code);
        return path;
    }

    /// <summary>Creates a callable owned by the runtime.</summary>
    public ScriptCallable Fn(NullFunction fn, string? name = null) => Runtime.CreateCallable(fn, name);

    /// <summary>Subscribes a lambda as <paramref name="script"/> (the way a script's top-level code would).</summary>
    public Guid Subscribe(string script, string kind, string key, NullFunction fn, BindingOptions? options = null, string? name = null)
    {
        using (Host.EnterScript(script))
        {
            return Host.Subscribe(kind, key, Fn(fn, name), options ?? new BindingOptions());
        }
    }

    /// <summary>A Map value from pairs.</summary>
    public static ScriptValue Map(params (string Key, ScriptValue Value)[] items)
        => ScriptValue.Map(items.ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal));

    /// <summary>A plain event.</summary>
    public static ScriptEvent Event(string type, string? vesselId = null, double ut = 0, params (string Key, ScriptValue Value)[] payload)
        => new(ut, type, vesselId, payload.ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal));

    public void Dispose()
    {
        Watchdog.Dispose();
        Runtime.Dispose();
        try
        {
            Directory.Delete(ScriptsDir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }

    private void OnScriptError(string script, Exception error)
    {
        Errors.Add((script, error));
        Host?.RecordError(script, error);
    }
}
