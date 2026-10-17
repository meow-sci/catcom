using Catcom.Core.Catalog;
using Catcom.Core.Hooks;
using Catcom.Core.Hosting;
using Catcom.Core.Logging;
using Catcom.Core.Runtime;
using Catcom.Core.Terminals;
using Catcom.Core.Tests.Support;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Catalog;

[TestFixture]
public sealed class ScriptCatalogLifecycleTests
{
    private static IReadOnlyDictionary<string, NullFunction> Exports(params (string Name, NullFunction Fn)[] items)
        => items.ToDictionary(i => i.Name, i => i.Fn, StringComparer.Ordinal);

    private static readonly NullFunction Noop = (_, _) => ScriptValue.Null;

    /// <summary>Registers a script whose top-level code subscribes to <paramref name="eventName"/> (like <c>ksa.on</c>).</summary>
    private static void RegisterWithHook(TestRig rig, string script, string eventName, string export, NullFunction handler,
        params (string Name, NullFunction Fn)[] extraExports)
    {
        rig.Runtime.Register(
            script,
            Exports(extraExports),
            (host, runtime) => host.Subscribe("event", eventName, runtime.CreateCallable(handler, export), new BindingOptions()));
    }

    private sealed class RecordingSink : ITerminalSink
    {
        public List<string> Writes { get; } = [];

        public List<string> Terminations { get; } = [];

        public void Write(ReadOnlySpan<byte> bytes) => Writes.Add(System.Text.Encoding.UTF8.GetString(bytes));

        public void Terminate(string message) => Terminations.Add(message);
    }

    // ---------------------------------------------------------------- load

    [Test]
    public void Load_RunsTheScript_RegistersCodeDeclaredHooks_AndReportsExports()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        RegisterWithHook(rig, "a.lua", "stage", "on_stage", Noop, ("main", Noop), ("helper", Noop));

        var info = rig.Catalog.Load("a.lua");

        var binding = rig.Hooks.ForScript("a.lua").Single();
        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Running));
            Assert.That(info.LastError, Is.Null);
            Assert.That(info.Exports, Is.EquivalentTo(new[] { "main", "helper" }));
            Assert.That(info.Language, Is.EqualTo(ScriptLanguages.Lua));
            Assert.That(binding.FromCode, Is.True);
            Assert.That(binding.Export, Is.EqualTo("on_stage"));
            Assert.That(binding.Kind, Is.EqualTo(BindingKind.Event));
            Assert.That(binding.Key, Is.EqualTo("stage"));
            Assert.That(rig.Catalog.Get("a.lua"), Is.EqualTo(info));
        });
    }

    [Test]
    public void Load_AttributesHostCallsDuringTopLevelCodeToTheScript()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        string? seen = null;
        rig.Runtime.Register("a.lua", Exports(), (host, _) => seen = host.CurrentScript);

        rig.Catalog.Load("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.EqualTo("a.lua"));
            Assert.That(rig.Host.CurrentScript, Is.Empty);
        });
    }

    [Test]
    public void Load_SetsTheScriptPath_ForContexts()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("dir/a.lua");

        rig.Catalog.Load("dir/a.lua");

        Assert.That(rig.Scheduler.BuildContext("tick", "dir/a.lua")["script"]["path"].AsString(), Is.EqualTo(path));
    }

    [Test]
    public void Load_RaisesChangedWithTheResultingInfo()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        var changed = new List<ScriptInfo>();
        rig.Catalog.Changed += changed.Add;

        rig.Catalog.Load("a.lua");

        Assert.That(changed.Last().State, Is.EqualTo(ScriptState.Running));
        Assert.That(changed.Last().Name, Is.EqualTo("a.lua"));
    }

    [Test]
    public void Load_OfAnUnknownScript_ReturnsAnErrorInfoWithoutThrowing()
    {
        using var rig = new TestRig();

        var missing = rig.Catalog.Load("nope.lua");
        var escaping = rig.Catalog.Load("../outside.lua");
        var rooted = rig.Catalog.Load("/etc/passwd");
        var wrongType = rig.Catalog.Load("readme.txt");

        Assert.Multiple(() =>
        {
            Assert.That(missing.State, Is.EqualTo(ScriptState.Error));
            Assert.That(missing.LastError, Does.Contain("not found"));
            Assert.That(escaping.State, Is.EqualTo(ScriptState.Error));
            Assert.That(rooted.State, Is.EqualTo(ScriptState.Error));
            Assert.That(wrongType.State, Is.EqualTo(ScriptState.Error));
            Assert.That(rig.Catalog.All(), Is.Empty, "failed lookups do not register entries");
        });
    }

    [Test]
    public void Load_WhenTheRuntimeIsUnavailable_EntersTheErrorState_WithTheReason()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Runtime.MarkUnavailable("native library missing");

        var info = rig.Catalog.Load("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Error));
            Assert.That(info.LastError, Is.EqualTo("runtime 'lua' not available: native library missing"));
            Assert.That(info.Exports, Is.Empty);
        });
    }

    [Test]
    public void Load_WhenNoRuntimeHandlesTheLanguage_EntersTheErrorState()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.ts");

        var info = rig.Catalog.Load("a.ts");

        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Error));
            Assert.That(info.LastError, Does.StartWith("runtime 'typescript' not available"));
        });
    }

    [Test]
    public void Load_AFirstLoadWithASyntaxError_EntersTheErrorState_WithFileLineAndColumn()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("a.lua", "local x = @syntax-error\n");

        var info = rig.Catalog.Load("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Error));
            Assert.That(info.LastError, Is.EqualTo($"{path}:1:1: syntax error"));
            Assert.That(info.Exports, Is.Empty);
            Assert.That(rig.Hooks.All(), Is.Empty);
        });
    }

    [Test]
    public void Load_AnUnreadableFile_EntersTheErrorState()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("a.lua");
        rig.Catalog.Scan();
        File.Delete(path);

        var info = rig.Catalog.Load("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Error));
            Assert.That(info.LastError, Does.Contain("cannot read"));
        });
    }

    // ---------------------------------------------------------------- reload

    [Test]
    public void Reload_SwapsTheHooksAtomically_OldIdsGone_NewPresent_OldCallablesDisposed()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        RegisterWithHook(rig, "a.lua", "stage", "on_stage_v1", Noop);
        rig.Catalog.Load("a.lua");
        var oldBinding = rig.Hooks.ForScript("a.lua").Single();
        var oldFn = rig.Hooks.ForEvent("stage", null, false).Single().Fn;

        RegisterWithHook(rig, "a.lua", "stage", "on_stage_v2", Noop);
        RegisterWithHookExtra(rig, "a.lua");
        rig.Catalog.Reload("a.lua");

        var bindings = rig.Hooks.ForScript("a.lua");
        Assert.Multiple(() =>
        {
            Assert.That(bindings.Select(b => b.Export), Is.EqualTo(new[] { "on_stage_v2", "on_tick_v2" }));
            Assert.That(bindings.Select(b => b.Id), Does.Not.Contain(oldBinding.Id));
            Assert.That(oldFn.IsDisposed, Is.True);
            Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Running));
            Assert.That(rig.Runtime.Loaded, Has.Count.EqualTo(1), "the old module was unloaded");
        });
    }

    private static void RegisterWithHookExtra(TestRig rig, string script)
    {
        rig.Runtime.Register(script, Exports(), (host, runtime) =>
        {
            host.Subscribe("event", "stage", runtime.CreateCallable(Noop, "on_stage_v2"), new BindingOptions());
            host.Subscribe("tick", "frame", runtime.CreateCallable(Noop, "on_tick_v2"), new BindingOptions());
        });
    }

    [Test]
    public void Reload_AFailingReload_KeepsTheOldModuleAndHooks_AndRecordsTheError()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("a.lua");
        var calls = new List<string>();
        RegisterWithHook(rig, "a.lua", "stage", "on_stage", (_, _) => { calls.Add("old"); return ScriptValue.Null; }, ("main", Noop));
        rig.Catalog.Load("a.lua");
        var oldBinding = rig.Hooks.ForScript("a.lua").Single();

        rig.WriteScript("a.lua", "-- now broken @syntax-error\n");
        rig.Catalog.Reload("a.lua");

        var info = rig.Catalog.Get("a.lua")!;
        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Running), "the old version is still live");
            Assert.That(info.LastError, Is.EqualTo($"{path}:1:1: syntax error"));
            Assert.That(info.Exports, Is.EqualTo(new[] { "main" }));
            Assert.That(rig.Hooks.ForScript("a.lua").Single().Id, Is.EqualTo(oldBinding.Id));
            Assert.That(rig.Runtime.Loaded, Is.EqualTo(new[] { "a.lua" }));
        });

        rig.Events.Publish(TestRig.Event("stage"));
        rig.Slot(1);
        Assert.That(calls, Is.EqualTo(new[] { "old" }), "the old hook still fires");
    }

    [Test]
    public void Reload_ATopLevelThatThrowsAfterSubscribing_RemovesTheHooksOfTheFailedAttempt()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        RegisterWithHook(rig, "a.lua", "stage", "old_hook", Noop);
        rig.Catalog.Load("a.lua");
        var oldBinding = rig.Hooks.ForScript("a.lua").Single();

        rig.Runtime.Register("a.lua", Exports(), (host, runtime) =>
        {
            host.Subscribe("event", "stage", runtime.CreateCallable(Noop, "half_registered"), new BindingOptions());
            throw new InvalidOperationException("top-level exploded");
        });
        rig.Catalog.Reload("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(rig.Hooks.ForScript("a.lua").Select(b => b.Id), Is.EqualTo(new[] { oldBinding.Id }));
            Assert.That(rig.Catalog.Get("a.lua")!.LastError, Does.Contain("top-level exploded"));
            Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Running));
        });
    }

    [Test]
    public void Reload_AfterAFailure_ClearsTheError()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua", "@syntax-error\n");
        rig.Catalog.Load("a.lua");
        Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Error));

        rig.WriteScript("a.lua", "fixed\n");
        rig.Catalog.Reload("a.lua");

        var info = rig.Catalog.Get("a.lua")!;
        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Running));
            Assert.That(info.LastError, Is.Null);
        });
    }

    [Test]
    public void Reload_CancelsTheWaitsOfTheOldModule_ButNotThoseOfTheNewOne()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        HostWait? oldWait = null;
        HostWait? newWait = null;
        rig.Runtime.Register("a.lua", Exports(), (host, _) => oldWait = host.Wait("sleep", ScriptValue.From(1000)));
        rig.Catalog.Load("a.lua");
        rig.Runtime.Register("a.lua", Exports(), (host, _) => newWait = host.Wait("sleep", ScriptValue.From(1000)));

        rig.Catalog.Reload("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(oldWait!.Task.IsCanceled, Is.True);
            Assert.That(newWait!.IsCompleted, Is.False);
            Assert.That(rig.Scheduler.PendingWaitIds("a.lua"), Is.EqualTo(new[] { newWait!.Id }));
        });
    }

    [Test]
    public void Reload_AFailedReload_KeepsTheOldWaits()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        HostWait? oldWait = null;
        rig.Runtime.Register("a.lua", Exports(), (host, _) => oldWait = host.Wait("sleep", ScriptValue.From(1000)));
        rig.Catalog.Load("a.lua");

        rig.WriteScript("a.lua", "@syntax-error");
        rig.Catalog.Reload("a.lua");

        Assert.That(oldWait!.IsCompleted, Is.False);
    }

    [Test]
    public void Load_ResetsTheFaultLatchOfTheScript()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Catalog.Load("a.lua");
        for (var i = 0; i < 3; i++)
            rig.Scheduler.ReportError("a.lua", new ScriptRuntimeException("boom"));
        Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Disabled));

        rig.Catalog.Reload("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(rig.Scheduler.IsDisabled("a.lua"), Is.False);
            Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Running));
            Assert.That(rig.Catalog.Get("a.lua")!.LastError, Is.Null);
        });
    }

    // ---------------------------------------------------------------- stored bindings

    [Test]
    public void Load_AppliesTheStoredEnabledBindingsOfTheScript()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Runtime.Register("a.lua", Exports(("on_ignite", Noop), ("sample", Noop), ("unused", Noop)));
        rig.Bindings.Save(
        [
            new BindingRecord("a.lua", "on_ignite", "event", "engine.ignite", true, "active", "ut", 0, true),
            new BindingRecord("a.lua", "sample", "tick", "every", false, null, "wall", 2.5, true),
            new BindingRecord("a.lua", "unused", "event", "stage", false, null, "ut", 0, false),
            new BindingRecord("b.lua", "other", "event", "stage", false, null, "ut", 0, true),
        ]);

        var info = rig.Catalog.Load("a.lua");

        var bindings = rig.Hooks.ForScript("a.lua");
        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Running));
            Assert.That(info.LastError, Is.Null);
            Assert.That(bindings, Has.Count.EqualTo(2));
            Assert.That(bindings.All(b => !b.FromCode), Is.True);
            Assert.That(bindings[0].Export, Is.EqualTo("on_ignite"));
            Assert.That(bindings[0].Kind, Is.EqualTo(BindingKind.Event));
            Assert.That(bindings[0].Key, Is.EqualTo("engine.ignite"));
            Assert.That(bindings[0].Options, Is.EqualTo(new BindingOptions(true, "active", "ut", 0)));
            Assert.That(bindings[1].Kind, Is.EqualTo(BindingKind.Tick));
            Assert.That(bindings[1].Options, Is.EqualTo(new BindingOptions(false, null, "wall", 2.5)));
        });
    }

    [Test]
    public void Load_AStoredBindingForAMissingExport_RecordsAWarning_ButTheScriptStaysRunning()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Runtime.Register("a.lua", Exports(("present", Noop)));
        rig.Bindings.Save(
        [
            new BindingRecord("a.lua", "gone", "event", "stage", false, null, "ut", 0, true),
            new BindingRecord("a.lua", "present", "event", "stage", false, null, "ut", 0, true),
            new BindingRecord("a.lua", "present", "nonsense", "stage", false, null, "ut", 0, true),
        ]);

        var info = rig.Catalog.Load("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Running));
            Assert.That(info.LastError, Does.Contain("binding: export 'gone' not found"));
            Assert.That(info.LastError, Does.Contain("unknown kind 'nonsense'"));
            Assert.That(rig.Hooks.ForScript("a.lua").Select(b => b.Export), Is.EqualTo(new[] { "present" }));
        });
    }

    [Test]
    public void Reload_ReplacesBothCodeAndStoredBindings()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        RegisterWithHook(rig, "a.lua", "stage", "code_hook", Noop, ("ui_hook", Noop));
        rig.Bindings.Save([new BindingRecord("a.lua", "ui_hook", "event", "stage", false, null, "ut", 0, true)]);
        rig.Catalog.Load("a.lua");
        var before = rig.Hooks.ForScript("a.lua").Select(b => b.Id).ToHashSet();

        rig.Catalog.Reload("a.lua");

        var after = rig.Hooks.ForScript("a.lua");
        Assert.Multiple(() =>
        {
            Assert.That(after.Select(b => b.Export), Is.EqualTo(new[] { "code_hook", "ui_hook" }));
            Assert.That(after.Any(b => before.Contains(b.Id)), Is.False);
        });
    }

    [Test]
    public void ApplyBindings_ReplacesOnlyTheUiBindings()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        RegisterWithHook(rig, "a.lua", "stage", "code_hook", Noop, ("first", Noop), ("second", Noop), ("main", Noop));
        rig.Bindings.Save([new BindingRecord("a.lua", "first", "event", "stage", false, null, "ut", 0, true)]);
        rig.Catalog.Start("a.lua");
        Assert.That(rig.Hooks.ForScript("a.lua").Select(b => b.Export), Is.EqualTo(new[] { "code_hook", "first", "main" }));

        rig.Bindings.Save([new BindingRecord("a.lua", "second", "tick", "frame", false, null, "ut", 0, true)]);
        rig.Catalog.ApplyBindings("a.lua");

        var bindings = rig.Hooks.ForScript("a.lua");
        Assert.Multiple(() =>
        {
            Assert.That(bindings.Select(b => b.Export), Is.EqualTo(new[] { "code_hook", "main", "second" }));
            Assert.That(bindings.Single(b => b.Export == "main").Kind, Is.EqualTo(BindingKind.Service), "the started service survives");
            Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Service));
        });
    }

    [Test]
    public void ApplyBindings_ClearsAStaleBindingWarning_AndIgnoresUnloadedScripts()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.WriteScript("idle.lua");
        rig.Runtime.Register("a.lua", Exports(("present", Noop)));
        rig.Bindings.Save([new BindingRecord("a.lua", "missing", "event", "stage", false, null, "ut", 0, true)]);
        rig.Catalog.Load("a.lua");
        rig.Catalog.Scan();
        Assert.That(rig.Catalog.Get("a.lua")!.LastError, Does.Contain("binding:"));

        rig.Bindings.Save([new BindingRecord("a.lua", "present", "event", "stage", false, null, "ut", 0, true)]);
        rig.Catalog.ApplyBindings("a.lua");
        rig.Catalog.ApplyBindings("idle.lua");
        rig.Catalog.ApplyBindings("unknown.lua");

        Assert.Multiple(() =>
        {
            Assert.That(rig.Catalog.Get("a.lua")!.LastError, Is.Null);
            Assert.That(rig.Hooks.ForScript("a.lua").Select(b => b.Export), Is.EqualTo(new[] { "present" }));
            Assert.That(rig.Hooks.ForScript("idle.lua"), Is.Empty);
        });
    }

    // ---------------------------------------------------------------- run

    [Test]
    public void Run_InvokesMainOnce_WithAMainContext_AndLeavesTheScriptRunning()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("a.lua");
        var contexts = new List<ScriptValue>();
        rig.Runtime.Register("a.lua", Exports(("main", (_, args) => { contexts.Add(args[0]); return ScriptValue.Null; })));

        rig.Catalog.Run("a.lua");

        Assert.That(contexts, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(contexts[0]["$ctx"].AsBool(), Is.True);
            Assert.That(contexts[0]["kind"].AsString(), Is.EqualTo("main"));
            Assert.That(contexts[0]["script"]["name"].AsString(), Is.EqualTo("a.lua"));
            Assert.That(contexts[0]["script"]["path"].AsString(), Is.EqualTo(path));
            Assert.That(contexts[0]["signal"].AsString(), Is.EqualTo("a.lua"));
            Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Running));
            Assert.That(rig.Hooks.ForScript("a.lua"), Is.Empty, "Run adds no service");
        });
    }

    [Test]
    public void Run_AlwaysReloadsFromDisk_BeforeCallingMain()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        var loads = 0;
        var mains = 0;
        rig.Runtime.Register("a.lua", Exports(("main", (_, _) => { mains++; return ScriptValue.Null; })), (_, _) => loads++);

        rig.Catalog.Run("a.lua");
        rig.Catalog.Run("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(loads, Is.EqualTo(2));
            Assert.That(mains, Is.EqualTo(2));
            Assert.That(rig.Runtime.Loaded, Is.EqualTo(new[] { "a.lua" }));
        });
    }

    [Test]
    public void Run_WithoutAMain_OnlyRunsTheTopLevelCode()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        var loads = 0;
        rig.Runtime.Register("a.lua", Exports(("helper", Noop)), (_, _) => loads++);

        rig.Catalog.Run("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(loads, Is.EqualTo(1));
            Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Running));
            Assert.That(rig.Catalog.Get("a.lua")!.LastError, Is.Null);
        });
    }

    [Test]
    public void Run_AFailedLoad_DoesNotCallMain()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua", "@syntax-error");
        var mains = 0;
        rig.Runtime.Register("a.lua", Exports(("main", (_, _) => { mains++; return ScriptValue.Null; })));

        rig.Catalog.Run("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(mains, Is.Zero);
            Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Error));
        });
    }

    [Test]
    public void Run_AMainThatThrows_IsReportedAsAScriptError_NotThrown()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Runtime.Register("a.lua", Exports(("main", (_, _) => throw new InvalidOperationException("main failed"))));

        Assert.DoesNotThrow(() => rig.Catalog.Run("a.lua"));

        Assert.That(rig.Errors.Single().Error.Message, Is.EqualTo("main failed"));
    }

    [Test]
    public void Run_AnAsyncMain_SuspendsAndFinishesInTheScheduler()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        var finished = false;
        rig.Runtime.Register("a.lua", Exports(("main", (host, _) =>
            rig.Runtime.Await(host.Wait("nextFrame", ScriptValue.Null), _ => { finished = true; return ScriptValue.Null; }))));
        rig.Slot(1);

        rig.Catalog.Run("a.lua");
        Assert.That(finished, Is.False);
        rig.Slot(2);

        Assert.That(finished, Is.True);
    }

    // ---------------------------------------------------------------- start / stop

    [Test]
    public void Start_AddsAServiceBinding_ThatTheSchedulerStartsInTheNextSlot_AsAServiceContext()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        var contexts = new List<ScriptValue>();
        rig.Runtime.Register("a.lua", Exports(("main", (_, args) => { contexts.Add(args[0]); return ScriptValue.Null; })));

        rig.Catalog.Start("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Service));
            Assert.That(contexts, Is.Empty, "the service starts in the next Step slot, not inline");
            Assert.That(rig.Hooks.Services().Single().Binding.Script, Is.EqualTo("a.lua"));
            Assert.That(rig.Hooks.Services().Single().Binding.FromCode, Is.False);
        });

        rig.Slot(1);
        rig.Slot(2);

        Assert.That(contexts, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(contexts[0]["kind"].AsString(), Is.EqualTo("service"));
            Assert.That(contexts[0]["signal"].AsString(), Is.EqualTo("a.lua"));
            Assert.That(contexts[0].AsMap().ContainsKey("termId"), Is.False);
            Assert.That(rig.Scheduler.IsCancelled("a.lua"), Is.False);
        });
    }

    [Test]
    public void Start_OfATerminalScript_CreatesTheTerminalFirst_AndStartsItAsATerminalContext()
    {
        using var rig = new TestRig();
        rig.WriteScript("door.lua", "-- @ksa terminal=true\n");
        ScriptValue? ctx = null;
        var terminalExistedAtLoad = false;
        rig.Runtime.Register("door.lua", Exports(("main", (_, args) => { ctx = args[0]; return ScriptValue.Null; })),
            (_, _) => terminalExistedAtLoad = rig.Terminals.ForScript("door.lua") is not null);

        rig.Catalog.Start("door.lua");
        var terminal = rig.Terminals.ForScript("door.lua");
        Assert.That(terminal, Is.Not.Null);
        rig.Slot(1);

        Assert.Multiple(() =>
        {
            Assert.That(terminalExistedAtLoad, Is.False, "created after load, before main runs");
            Assert.That(ctx!["kind"].AsString(), Is.EqualTo("terminal"));
            Assert.That(ctx["termId"].AsNumber(), Is.EqualTo(terminal!.Id));
            Assert.That(rig.Catalog.Get("door.lua")!.HasTerminal, Is.True);
        });
    }

    [Test]
    public void Start_WithoutAMain_FailsWithAnError()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Runtime.Register("a.lua", Exports(("helper", Noop)));

        rig.Catalog.Start("a.lua");

        var info = rig.Catalog.Get("a.lua")!;
        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Error));
            Assert.That(info.LastError, Is.EqualTo("no main(ctx) export"));
            Assert.That(rig.Hooks.Services(), Is.Empty);
        });
    }

    [Test]
    public void Start_ARunningService_RestartsWithoutDuplicatingTheServiceBinding()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        var starts = 0;
        rig.Runtime.Register("a.lua", Exports(("main", (_, _) => { starts++; return ScriptValue.Null; })));

        rig.Catalog.Start("a.lua");
        rig.Slot(1);
        rig.Catalog.Start("a.lua");
        rig.Slot(2);

        Assert.Multiple(() =>
        {
            Assert.That(starts, Is.EqualTo(2));
            Assert.That(rig.Hooks.Services(), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Stop_SignalsCancel_DeliversSignalCancel_Unloads_AndTerminatesTheTerminal()
    {
        using var rig = new TestRig();
        rig.WriteScript("door.lua", "-- @ksa terminal=true\n");
        var cancelEvents = new List<ScriptValue>();
        rig.Runtime.Register(
            "door.lua",
            Exports(("main", Noop)),
            (host, runtime) => host.Subscribe(
                "event",
                "signal.cancel",
                runtime.CreateCallable((_, args) => { cancelEvents.Add(args[0]["event"]); return ScriptValue.Null; }, "onCancel"),
                new BindingOptions()));
        rig.Catalog.Start("door.lua");
        rig.Slot(1);
        var terminal = rig.Terminals.ForScript("door.lua")!;
        var sink = new RecordingSink();
        terminal.Attach(sink, 80, 24);
        sink.Writes.Clear();

        rig.Catalog.Stop("door.lua");

        var info = rig.Catalog.Get("door.lua")!;
        Assert.Multiple(() =>
        {
            Assert.That(cancelEvents, Has.Count.EqualTo(1));
            Assert.That(cancelEvents[0]["type"].AsString(), Is.EqualTo("signal.cancel"));
            Assert.That(rig.Scheduler.IsCancelled("door.lua"), Is.True);
            Assert.That(rig.Host.Read("signal:door.lua", "cancelled").AsBool(), Is.True);
            Assert.That(info.State, Is.EqualTo(ScriptState.Idle));
            Assert.That(info.Exports, Is.Empty);
            Assert.That(rig.Hooks.ForScript("door.lua"), Is.Empty);
            Assert.That(rig.Runtime.Loaded, Is.Empty);
            Assert.That(sink.Terminations, Is.EqualTo(new[] { "[script exited]" }));
            Assert.That(sink.Writes.Last(), Does.Contain("[script exited]"));
            Assert.That(rig.Terminals.ForScript("door.lua"), Is.Null);
            Assert.That(rig.Terminals.Get(terminal.Id), Is.Null);
        });
    }

    [Test]
    public void Stop_OnlySignalsTheStoppedScript()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.WriteScript("b.lua");
        var cancelled = new List<string>();
        foreach (var name in new[] { "a.lua", "b.lua" })
        {
            var captured = name;
            rig.Runtime.Register(name, Exports(("main", Noop)), (host, runtime) =>
                host.Subscribe("event", "signal.cancel", runtime.CreateCallable((_, _) => { cancelled.Add(captured); return ScriptValue.Null; }), new BindingOptions()));
            rig.Catalog.Start(name);
        }

        rig.Catalog.Stop("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(cancelled, Is.EqualTo(new[] { "a.lua" }));
            Assert.That(rig.Catalog.Get("b.lua")!.State, Is.EqualTo(ScriptState.Service));
            Assert.That(rig.Scheduler.IsCancelled("b.lua"), Is.False);
        });
    }

    [Test]
    public void LoadingAgainAfterStop_ClearsTheCancelSignal()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Runtime.Register("a.lua", Exports(("main", Noop)));
        rig.Catalog.Start("a.lua");
        rig.Catalog.Stop("a.lua");
        Assert.That(rig.Scheduler.IsCancelled("a.lua"), Is.True);

        rig.Catalog.Run("a.lua");

        Assert.That(rig.Scheduler.IsCancelled("a.lua"), Is.False);
    }

    [Test]
    public void Stop_OfAnUnknownScript_IsANoOp()
    {
        using var rig = new TestRig();

        Assert.DoesNotThrow(() => rig.Catalog.Stop("nope.lua"));
    }

    [Test]
    public void Unload_CancelsWaits_RemovesHooks_ReleasesTheModule_AndReturnsToIdle()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        HostWait? wait = null;
        rig.Runtime.Register("a.lua", Exports(("main", Noop)), (host, runtime) =>
        {
            host.Subscribe("event", "stage", runtime.CreateCallable(Noop, "on_stage"), new BindingOptions());
            wait = host.Wait("sleep", ScriptValue.From(1000));
        });
        rig.Catalog.Load("a.lua");

        rig.Catalog.Unload("a.lua");

        var info = rig.Catalog.Get("a.lua")!;
        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Idle));
            Assert.That(info.Exports, Is.Empty);
            Assert.That(wait!.Task.IsCanceled, Is.True);
            Assert.That(rig.Hooks.ForScript("a.lua"), Is.Empty);
            Assert.That(rig.Runtime.Loaded, Is.Empty);
            Assert.That(rig.Scheduler.Stats.PendingWaits, Is.Zero);
        });
    }

    [Test]
    public void Reload_OfARunningService_RestartsItWithTheNewVersion_KeepingTheTerminal()
    {
        using var rig = new TestRig();
        rig.WriteScript("door.lua", "-- @ksa terminal=true\n");
        var started = new List<string>();
        rig.Runtime.Register("door.lua", Exports(("main", (_, _) => { started.Add("v1"); return ScriptValue.Null; })));
        rig.Catalog.Start("door.lua");
        rig.Slot(1);
        var terminal = rig.Terminals.ForScript("door.lua");

        rig.Runtime.Register("door.lua", Exports(("main", (_, _) => { started.Add("v2"); return ScriptValue.Null; })));
        rig.Catalog.Reload("door.lua");
        Assert.That(rig.Catalog.Get("door.lua")!.State, Is.EqualTo(ScriptState.Service));
        rig.Slot(2);

        Assert.Multiple(() =>
        {
            Assert.That(started, Is.EqualTo(new[] { "v1", "v2" }));
            Assert.That(rig.Terminals.ForScript("door.lua"), Is.SameAs(terminal));
            Assert.That(rig.Hooks.Services(), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Reload_OfARunningService_WhoseNewVersionFails_KeepsTheOldServiceRunning()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Runtime.Register("a.lua", Exports(("main", Noop)));
        rig.Catalog.Start("a.lua");

        rig.WriteScript("a.lua", "@syntax-error");
        rig.Catalog.Reload("a.lua");

        var info = rig.Catalog.Get("a.lua")!;
        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Service));
            Assert.That(info.LastError, Does.Contain("syntax error"));
            Assert.That(rig.Hooks.Services(), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Reload_OfAServiceWhoseNewVersionHasNoMain_EntersTheErrorState()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Runtime.Register("a.lua", Exports(("main", Noop)));
        rig.Catalog.Start("a.lua");

        rig.Runtime.Register("a.lua", Exports(("helper", Noop)));
        rig.Catalog.Reload("a.lua");

        var info = rig.Catalog.Get("a.lua")!;
        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Error));
            Assert.That(info.LastError, Is.EqualTo("no main(ctx) export"));
            Assert.That(rig.Hooks.Services(), Is.Empty);
        });
    }

    // ---------------------------------------------------------------- disabled

    [Test]
    public void ADisabledScript_ShowsTheDisabledStateAndReason_AndAReloadRevivesIt()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Runtime.Register("a.lua", Exports(("main", Noop)), (host, runtime) =>
            host.Subscribe("tick", "frame", runtime.CreateCallable((_, _) => throw new InvalidOperationException("boom"), "tick"), new BindingOptions()));
        rig.Catalog.Load("a.lua");
        var changed = new List<ScriptInfo>();
        rig.Catalog.Changed += changed.Add;

        for (var i = 1; i <= 3; i++)
            rig.Slot(i);

        var info = rig.Catalog.Get("a.lua")!;
        Assert.Multiple(() =>
        {
            Assert.That(info.State, Is.EqualTo(ScriptState.Disabled));
            Assert.That(info.LastError, Does.Contain("fault limit reached"));
            Assert.That(changed.Last().State, Is.EqualTo(ScriptState.Disabled));
        });

        rig.Catalog.Reload("a.lua");
        Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Running));
    }

    [Test]
    public void ADisabledService_RestartsOnReload()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        var starts = 0;
        rig.Runtime.Register("a.lua", Exports(("main", (_, _) => { starts++; return ScriptValue.Null; })));
        rig.Catalog.Start("a.lua");
        rig.Slot(1);
        for (var i = 0; i < 3; i++)
            rig.Scheduler.ReportError("a.lua", new ScriptRuntimeException("async failure"));
        Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Disabled));

        rig.Catalog.Reload("a.lua");
        rig.Slot(2);

        Assert.Multiple(() =>
        {
            Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Service));
            Assert.That(starts, Is.EqualTo(2));
        });
    }

    // ---------------------------------------------------------------- file changes

    [Test]
    public void OnFileChanged_OfALoadedScript_ReloadsIt()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("a.lua");
        RegisterWithHook(rig, "a.lua", "stage", "v1", Noop);
        rig.Catalog.Load("a.lua");

        RegisterWithHook(rig, "a.lua", "stage", "v2", Noop);
        rig.WriteScript("a.lua", "-- edited\n");
        rig.Catalog.OnFileChanged(path);

        Assert.That(rig.Hooks.ForScript("a.lua").Single().Export, Is.EqualTo("v2"));
    }

    [Test]
    public void OnFileChanged_OfAScriptInTheErrorState_RetriesTheLoad()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("a.lua", "@syntax-error");
        rig.Catalog.Load("a.lua");
        Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Error));

        rig.WriteScript("a.lua", "fixed");
        rig.Catalog.OnFileChanged(path);

        Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Running));
    }

    [Test]
    public void OnFileChanged_OfAServiceScript_RestartsTheService()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("a.lua");
        var starts = 0;
        rig.Runtime.Register("a.lua", Exports(("main", (_, _) => { starts++; return ScriptValue.Null; })));
        rig.Catalog.Start("a.lua");
        rig.Slot(1);

        rig.WriteScript("a.lua", "-- edited\n");
        rig.Catalog.OnFileChanged(path);
        rig.Slot(2);

        Assert.Multiple(() =>
        {
            Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Service));
            Assert.That(starts, Is.EqualTo(2));
        });
    }

    [Test]
    public void OnFileChanged_OfAnIdleScript_OnlyRefreshesTheHeader()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("a.lua", "-- @ksa name=Old\n");
        rig.Catalog.Scan();
        var loads = 0;
        rig.Runtime.Register("a.lua", Exports(), (_, _) => loads++);
        var changed = new List<ScriptInfo>();
        rig.Catalog.Changed += changed.Add;

        rig.WriteScript("a.lua", "-- @ksa name=New autostart=true\n");
        rig.Catalog.OnFileChanged(path);

        Assert.Multiple(() =>
        {
            Assert.That(loads, Is.Zero);
            var info = rig.Catalog.Get("a.lua")!;
            Assert.That(info.State, Is.EqualTo(ScriptState.Idle));
            Assert.That(info.Title, Is.EqualTo("New"));
            Assert.That(info.Autostart, Is.True);
            Assert.That(changed, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void OnFileChanged_OfADeletedScript_UnloadsAndForgetsIt()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("a.lua");
        RegisterWithHook(rig, "a.lua", "stage", "hook", Noop);
        rig.Catalog.Load("a.lua");
        var changed = new List<ScriptInfo>();
        rig.Catalog.Changed += changed.Add;

        File.Delete(path);
        rig.Catalog.OnFileChanged(path);

        Assert.Multiple(() =>
        {
            Assert.That(rig.Catalog.Get("a.lua"), Is.Null);
            Assert.That(rig.Hooks.ForScript("a.lua"), Is.Empty);
            Assert.That(rig.Runtime.Loaded, Is.Empty);
            Assert.That(changed, Is.Not.Empty);
        });
    }

    [Test]
    public void OnFileChanged_OfADeletedTerminalScript_EndsItsTerminal()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("door.lua", "-- @ksa terminal=true\n");
        rig.Runtime.Register("door.lua", Exports(("main", Noop)));
        rig.Catalog.Start("door.lua");
        var sink = new RecordingSink();
        rig.Terminals.ForScript("door.lua")!.Attach(sink, 80, 24);

        File.Delete(path);
        rig.Catalog.OnFileChanged(path);

        Assert.Multiple(() =>
        {
            Assert.That(sink.Terminations, Has.Count.EqualTo(1));
            Assert.That(rig.Terminals.ForScript("door.lua"), Is.Null);
        });
    }

    [Test]
    public void OnFileChanged_OfANewScript_AddsItAsIdle()
    {
        using var rig = new TestRig();
        rig.Catalog.Scan();
        var path = rig.WriteScript("fresh.lua", "-- @ksa name=Fresh\n");
        var changed = new List<ScriptInfo>();
        rig.Catalog.Changed += changed.Add;

        rig.Catalog.OnFileChanged(path);

        var info = rig.Catalog.Get("fresh.lua");
        Assert.Multiple(() =>
        {
            Assert.That(info, Is.Not.Null);
            Assert.That(info!.State, Is.EqualTo(ScriptState.Idle));
            Assert.That(info.Title, Is.EqualTo("Fresh"));
            Assert.That(changed.Select(c => c.Name), Is.EqualTo(new[] { "fresh.lua" }));
        });
    }

    [Test]
    public void OnFileChanged_IgnoresLibStubsDeclarationsHiddenAndForeignFiles()
    {
        using var rig = new TestRig();
        var lib = rig.WriteScript("lib/tui.lua");
        var stubs = rig.WriteScript("stubs/ksa.lua");
        var decl = rig.WriteScript("types.d.ts");
        var hidden = rig.WriteScript(".git/hook.lua");
        var text = rig.WriteScript("notes.txt");
        var foreign = Path.Combine(Path.GetTempPath(), "elsewhere.lua");
        File.WriteAllText(foreign, "x");
        try
        {
            foreach (var path in new[] { lib, stubs, decl, hidden, text, foreign })
                rig.Catalog.OnFileChanged(path);
        }
        finally
        {
            File.Delete(foreign);
        }

        Assert.That(rig.Catalog.All(), Is.Empty);
    }

    // ---------------------------------------------------------------- robustness

    [Test]
    public void TheCatalog_NeverThrows_WhenTheRuntimeMisbehaves()
    {
        using var log = new LogCapture();
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Runtime.Dispose();

        Assert.DoesNotThrow(() =>
        {
            rig.Catalog.Load("a.lua");
            rig.Catalog.Run("a.lua");
            rig.Catalog.Start("a.lua");
            rig.Catalog.Reload("a.lua");
            rig.Catalog.Stop("a.lua");
            rig.Catalog.Unload("a.lua");
            rig.Catalog.ApplyBindings("a.lua");
        });
    }

    [Test]
    public void AThrowingChangedHandler_DoesNotBreakTheCatalog()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        rig.Catalog.Changed += _ => throw new InvalidOperationException("ui exploded");

        Assert.DoesNotThrow(() => rig.Catalog.Load("a.lua"));
        Assert.That(rig.Catalog.Get("a.lua")!.State, Is.EqualTo(ScriptState.Running));
    }

    [Test]
    public void All_ListsScriptsInOrdinalNameOrder()
    {
        using var rig = new TestRig();
        foreach (var name in new[] { "b.lua", "Z.lua", "a.lua", "dir/c.lua" })
            rig.WriteScript(name);

        rig.Catalog.Scan();

        Assert.That(rig.Catalog.All().Select(i => i.Name), Is.EqualTo(new[] { "Z.lua", "a.lua", "b.lua", "dir/c.lua" }));
    }
}
