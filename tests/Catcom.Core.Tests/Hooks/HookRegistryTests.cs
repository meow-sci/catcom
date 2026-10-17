using Catcom.Core.Hooks;
using Catcom.Core.Hosting;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Hooks;

[TestFixture]
public sealed class HookRegistryTests
{
    private int _disposed;

    private ScriptCallable Callable(string? name = "fn")
        => ScriptCallable.Create("test", Interlocked.Increment(ref _disposed) + 1000, name, _ => { });

    private static (ScriptCallable Fn, Func<int> Disposals) Tracked(string? name = "fn")
    {
        var count = 0;
        var fn = ScriptCallable.Create("test", 1, name, _ => count++);
        return (fn, () => count);
    }

    [Test]
    public void Add_RecordsTheBinding_AndNamesTheExportAfterTheCallable()
    {
        var hooks = new HookRegistry();
        var id = hooks.Add("a.lua", BindingKind.Event, "engine.ignite", Callable("on_ignite"), new BindingOptions(), fromCode: true);
        var anonymous = hooks.Add("a.lua", BindingKind.Tick, "frame", Callable(null), new BindingOptions(), fromCode: false);

        var all = hooks.All();
        Assert.Multiple(() =>
        {
            Assert.That(all, Has.Count.EqualTo(2));
            Assert.That(all[0].Id, Is.EqualTo(id));
            Assert.That(all[0].Script, Is.EqualTo("a.lua"));
            Assert.That(all[0].Export, Is.EqualTo("on_ignite"));
            Assert.That(all[0].Kind, Is.EqualTo(BindingKind.Event));
            Assert.That(all[0].Key, Is.EqualTo("engine.ignite"));
            Assert.That(all[0].FromCode, Is.True);
            Assert.That(all[0].Enabled, Is.True);
            Assert.That(all[1].Id, Is.EqualTo(anonymous));
            Assert.That(all[1].Export, Is.EqualTo("<anonymous>"));
            Assert.That(all[1].FromCode, Is.False);
        });
    }

    [Test]
    public void ForEvent_MatchesTypeExactly_AndSeparatesSyncFromAsync()
    {
        var hooks = new HookRegistry();
        hooks.Add("a.lua", BindingKind.Event, "engine.ignite", Callable("async"), new BindingOptions(), true);
        hooks.Add("a.lua", BindingKind.Event, "engine.ignite", Callable("sync"), new BindingOptions(Sync: true), true);
        hooks.Add("a.lua", BindingKind.Event, "Engine.Ignite", Callable("case"), new BindingOptions(), true);
        hooks.Add("a.lua", BindingKind.Event, "stage", Callable("other"), new BindingOptions(), true);
        hooks.Add("a.lua", BindingKind.Tick, "engine.ignite", Callable("tick"), new BindingOptions(), true);

        Assert.Multiple(() =>
        {
            Assert.That(hooks.ForEvent("engine.ignite", null, sync: false).Select(h => h.Binding.Export), Is.EqualTo(new[] { "async" }));
            Assert.That(hooks.ForEvent("engine.ignite", null, sync: true).Select(h => h.Binding.Export), Is.EqualTo(new[] { "sync" }));
            Assert.That(hooks.ForEvent("stage", null, sync: false).Select(h => h.Binding.Export), Is.EqualTo(new[] { "other" }));
            Assert.That(hooks.ForEvent("nothing", null, sync: false), Is.Empty);
        });
    }

    [TestCase(null, "v1", true)]
    [TestCase("", "v1", true)]
    [TestCase("any", "v1", true)]
    [TestCase("any", null, true)]
    [TestCase("v1", "v1", true)]
    [TestCase("v1", "v2", false)]
    [TestCase("v1", null, false)]
    [TestCase("active", "v1", true)]
    [TestCase("active", "v2", false)]
    [TestCase("active", null, false)]
    public void ForEvent_AppliesTheVesselFilter(string? filter, string? eventVessel, bool expected)
    {
        var hooks = new HookRegistry { ActiveVesselProvider = () => "v1" };
        hooks.Add("a.lua", BindingKind.Event, "e", Callable(), new BindingOptions(Vessel: filter), true);

        Assert.That(hooks.ForEvent("e", eventVessel, sync: false), Has.Count.EqualTo(expected ? 1 : 0));
    }

    [Test]
    public void ForEvent_ActiveFilter_NeverMatchesWithoutAnActiveVessel()
    {
        var hooks = new HookRegistry();
        hooks.Add("a.lua", BindingKind.Event, "e", Callable(), new BindingOptions(Vessel: "active"), true);

        Assert.That(hooks.ForEvent("e", "v1", sync: false), Is.Empty);

        hooks.ActiveVesselProvider = () => null;
        Assert.That(hooks.ForEvent("e", "v1", sync: false), Is.Empty);

        hooks.ActiveVesselProvider = () => throw new InvalidOperationException("no game");
        Assert.That(hooks.ForEvent("e", "v1", sync: false), Is.Empty);
    }

    [Test]
    public void Queries_ReturnOnlyEnabledBindings_AndSetEnabledReportsUnknownIds()
    {
        var hooks = new HookRegistry();
        var id = hooks.Add("a.lua", BindingKind.Event, "e", Callable(), new BindingOptions(), true);
        hooks.Add("a.lua", BindingKind.Tick, "frame", Callable(), new BindingOptions(), true);
        hooks.Add("a.lua", BindingKind.Service, "main", Callable(), new BindingOptions(), true);

        Assert.That(hooks.SetEnabled(id, false), Is.True);
        Assert.That(hooks.SetEnabled(Guid.NewGuid(), false), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(hooks.ForEvent("e", null, false), Is.Empty);
            Assert.That(hooks.All(), Has.Count.EqualTo(3), "All() lists disabled bindings too");
            Assert.That(hooks.All().Single(b => b.Id == id).Enabled, Is.False);
        });

        hooks.SetEnabled(id, true);
        Assert.That(hooks.ForEvent("e", null, false), Has.Count.EqualTo(1));

        foreach (var binding in hooks.All())
            hooks.SetEnabled(binding.Id, false);
        Assert.Multiple(() =>
        {
            Assert.That(hooks.ForTick("frame"), Is.Empty);
            Assert.That(hooks.Services(), Is.Empty);
        });
    }

    [Test]
    public void ForTick_Services_AndCommands_SelectByKindAndKey()
    {
        var hooks = new HookRegistry();
        hooks.Add("a.lua", BindingKind.Tick, "frame", Callable("f"), new BindingOptions(), true);
        hooks.Add("a.lua", BindingKind.Tick, "step", Callable("s"), new BindingOptions(), true);
        hooks.Add("b.lua", BindingKind.Tick, "every", Callable("e"), new BindingOptions(IntervalSeconds: 1), true);
        hooks.Add("a.lua", BindingKind.Service, "main", Callable("svc"), new BindingOptions(), false);
        hooks.Add("a.lua", BindingKind.Command, "Greet", Callable("cmd"), new BindingOptions(), true);
        hooks.Add("a.lua", BindingKind.Command, "other", Callable("cmd2"), new BindingOptions(), true);

        Assert.Multiple(() =>
        {
            Assert.That(hooks.ForTick("frame").Select(h => h.Binding.Export), Is.EqualTo(new[] { "f" }));
            Assert.That(hooks.ForTick("step").Select(h => h.Binding.Export), Is.EqualTo(new[] { "s" }));
            Assert.That(hooks.ForTick("every").Select(h => h.Binding.Export), Is.EqualTo(new[] { "e" }));
            Assert.That(hooks.Services().Select(h => h.Binding.Export), Is.EqualTo(new[] { "svc" }));
            Assert.That(hooks.Commands("greet").Select(h => h.Binding.Export), Is.EqualTo(new[] { "cmd" }));
            Assert.That(hooks.Commands("GREET"), Has.Count.EqualTo(1));
            Assert.That(hooks.Commands("missing"), Is.Empty);
        });
    }

    [Test]
    public void Queries_PreserveInsertionOrder_AndForScriptFilters()
    {
        var hooks = new HookRegistry();
        foreach (var name in new[] { "one", "two", "three" })
            hooks.Add("a.lua", BindingKind.Event, "e", Callable(name), new BindingOptions(), true);
        hooks.Add("b.lua", BindingKind.Event, "e", Callable("four"), new BindingOptions(), true);

        Assert.Multiple(() =>
        {
            Assert.That(hooks.ForEvent("e", null, false).Select(h => h.Binding.Export), Is.EqualTo(new[] { "one", "two", "three", "four" }));
            Assert.That(hooks.ForScript("a.lua").Select(b => b.Export), Is.EqualTo(new[] { "one", "two", "three" }));
            Assert.That(hooks.ForScript("b.lua"), Has.Count.EqualTo(1));
            Assert.That(hooks.ForScript("c.lua"), Is.Empty);
        });
    }

    [Test]
    public void Queries_ReturnSnapshots_SoCallersMayMutateWhileIterating()
    {
        var hooks = new HookRegistry();
        hooks.Add("a.lua", BindingKind.Event, "e", Callable("one"), new BindingOptions(), true);
        hooks.Add("a.lua", BindingKind.Event, "e", Callable("two"), new BindingOptions(), true);

        var visited = new List<string>();
        foreach (var (binding, _) in hooks.ForEvent("e", null, false))
        {
            visited.Add(binding.Export);
            hooks.Add("a.lua", BindingKind.Event, "e", Callable("added"), new BindingOptions(), true);
            hooks.RemoveAll("a.lua");
        }

        Assert.That(visited, Is.EqualTo(new[] { "one", "two" }));
    }

    [Test]
    public void Remove_DisposesTheCallable_AndIgnoresUnknownIds()
    {
        var hooks = new HookRegistry();
        var (fn, disposals) = Tracked();
        var id = hooks.Add("a.lua", BindingKind.Event, "e", fn, new BindingOptions(), true);

        hooks.Remove(Guid.NewGuid());
        Assert.That(disposals(), Is.Zero);

        hooks.Remove(id);
        Assert.Multiple(() =>
        {
            Assert.That(disposals(), Is.EqualTo(1));
            Assert.That(fn.IsDisposed, Is.True);
            Assert.That(hooks.All(), Is.Empty);
        });
    }

    [Test]
    public void RemoveAll_RemovesOnlyThatScript_AndDisposesItsCallables()
    {
        var hooks = new HookRegistry();
        var (a1, a1Disposals) = Tracked("a1");
        var (a2, a2Disposals) = Tracked("a2");
        var (b1, b1Disposals) = Tracked("b1");
        hooks.Add("a.lua", BindingKind.Event, "e", a1, new BindingOptions(), true);
        hooks.Add("a.lua", BindingKind.Tick, "frame", a2, new BindingOptions(), false);
        hooks.Add("b.lua", BindingKind.Event, "e", b1, new BindingOptions(), true);

        hooks.RemoveAll("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(a1Disposals() + a2Disposals(), Is.EqualTo(2));
            Assert.That(b1Disposals(), Is.Zero);
            Assert.That(hooks.All().Select(b => b.Script), Is.EqualTo(new[] { "b.lua" }));
        });
    }

    [Test]
    public void RemoveWhere_RemovesTheMatchingBindingsOfOneScript()
    {
        var hooks = new HookRegistry();
        var (code, codeDisposals) = Tracked("code");
        var (ui, uiDisposals) = Tracked("ui");
        var (other, otherDisposals) = Tracked("other");
        hooks.Add("a.lua", BindingKind.Event, "e", code, new BindingOptions(), fromCode: true);
        hooks.Add("a.lua", BindingKind.Event, "e", ui, new BindingOptions(), fromCode: false);
        hooks.Add("b.lua", BindingKind.Event, "e", other, new BindingOptions(), fromCode: false);

        hooks.RemoveWhere("a.lua", b => !b.FromCode);

        Assert.Multiple(() =>
        {
            Assert.That(uiDisposals(), Is.EqualTo(1));
            Assert.That(codeDisposals(), Is.Zero);
            Assert.That(otherDisposals(), Is.Zero);
            Assert.That(hooks.All().Select(b => b.Export), Is.EqualTo(new[] { "code", "other" }));
        });
    }

    [Test]
    public void Changed_IsRaisedAfterEveryMutation_ButNotForNoOps()
    {
        var hooks = new HookRegistry();
        var raised = 0;
        hooks.Changed += () => raised++;

        var id = hooks.Add("a.lua", BindingKind.Event, "e", Callable(), new BindingOptions(), true);
        Assert.That(raised, Is.EqualTo(1));

        hooks.SetEnabled(id, false);
        Assert.That(raised, Is.EqualTo(2));

        hooks.Remove(Guid.NewGuid());
        hooks.RemoveAll("nobody.lua");
        hooks.RemoveWhere("a.lua", _ => false);
        hooks.SetEnabled(Guid.NewGuid(), true);
        Assert.That(raised, Is.EqualTo(2), "no-ops raise nothing");

        hooks.RemoveWhere("a.lua", _ => true);
        Assert.That(raised, Is.EqualTo(3));

        hooks.Add("a.lua", BindingKind.Event, "e", Callable(), new BindingOptions(), true);
        hooks.RemoveAll("a.lua");
        Assert.That(raised, Is.EqualTo(5));
    }

    [Test]
    public void Changed_HandlerExceptions_AreSwallowed()
    {
        var hooks = new HookRegistry();
        hooks.Changed += () => throw new InvalidOperationException("ui exploded");

        Assert.DoesNotThrow(() => hooks.Add("a.lua", BindingKind.Event, "e", Callable(), new BindingOptions(), true));
    }

    [Test]
    public void DisposedCallables_AreNotDelivered()
    {
        var hooks = new HookRegistry();
        var fn = Callable();
        hooks.Add("a.lua", BindingKind.Event, "e", fn, new BindingOptions(), true);

        fn.Dispose();

        Assert.That(hooks.ForEvent("e", null, false), Is.Empty);
    }

    [Test]
    public void BindingKinds_RoundTripText_IgnoringCase()
    {
        foreach (var kind in Enum.GetValues<BindingKind>())
        {
            var text = BindingKinds.ToText(kind);
            Assert.That(text, Is.EqualTo(kind.ToString().ToLowerInvariant()));
            Assert.That(BindingKinds.TryParse(text.ToUpperInvariant(), out var parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(kind));
        }

        Assert.Multiple(() =>
        {
            Assert.That(BindingKinds.TryParse("terminal", out _), Is.False);
            Assert.That(BindingKinds.TryParse("", out _), Is.False);
            Assert.That(BindingKinds.TryParse(null, out _), Is.False);
        });
    }
}
