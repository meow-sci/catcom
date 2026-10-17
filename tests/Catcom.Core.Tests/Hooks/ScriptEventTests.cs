using Catcom.Core.Hooks;
using Catcom.Core.Tests.Support;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Hooks;

[TestFixture]
public sealed class ScriptEventTests
{
    [Test]
    public void ToScriptValue_BuildsTheMapScriptsSee()
    {
        var ev = TestRig.Event("engine.ignite", "v1", ut: 12.5, ("engine", ScriptValue.From("e0")));

        var map = ev.ToScriptValue();

        Assert.Multiple(() =>
        {
            Assert.That(map["ut"].AsNumber(), Is.EqualTo(12.5));
            Assert.That(map["type"].AsString(), Is.EqualTo("engine.ignite"));
            Assert.That(map["vesselId"].AsString(), Is.EqualTo("v1"));
            Assert.That(map["payload"]["engine"].AsString(), Is.EqualTo("e0"));
        });
    }

    [Test]
    public void ToScriptValue_UsesNullForAMissingVessel_AndHidesTheTargetingKey()
    {
        var ev = TestRig.Event("term.data", null, 0, ("$script", ScriptValue.From("a.lua")), ("termId", ScriptValue.From(3)));

        var map = ev.ToScriptValue();

        Assert.Multiple(() =>
        {
            Assert.That(map["vesselId"].IsNull, Is.True);
            Assert.That(map["payload"].AsMap().ContainsKey("$script"), Is.False);
            Assert.That(map["payload"]["termId"].AsNumber(), Is.EqualTo(3));
            Assert.That(ev.TargetScript, Is.EqualTo("a.lua"));
        });
    }

    [Test]
    public void TargetScript_IsNullForBroadcastAndNonStringTargets()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TestRig.Event("stage").TargetScript, Is.Null);
            Assert.That(TestRig.Event("stage", null, 0, ("$script", ScriptValue.From(1))).TargetScript, Is.Null);
        });
    }
}
