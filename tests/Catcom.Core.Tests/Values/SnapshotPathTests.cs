using Catcom.Core.Snapshots;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Values;

[TestFixture]
public sealed class SnapshotPathTests
{
    private static VesselSnapshot Vessel() => TestData.Vessel() with { CenterOfMass = new double3Snap(1, 2, 3) };

    // ---------------------------------------------------------------- raw record

    [Test]
    public void RawRecord_NameVariantsAllMatch()
    {
        var vessel = Vessel();
        Assert.Multiple(() =>
        {
            Assert.That(SnapshotPath.Resolve(vessel, "positionCci.x").AsNumber(), Is.EqualTo(1234.5));
            Assert.That(SnapshotPath.Resolve(vessel, "position_cci.x").AsNumber(), Is.EqualTo(1234.5));
            Assert.That(SnapshotPath.Resolve(vessel, "PositionCci.X").AsNumber(), Is.EqualTo(1234.5));
            Assert.That(SnapshotPath.Resolve(vessel, "position-cci.y").AsNumber(), Is.EqualTo(-6789.25));
        });
    }

    [Test]
    public void RawRecord_ListsAreIndexed()
    {
        var vessel = Vessel();
        Assert.Multiple(() =>
        {
            Assert.That(SnapshotPath.Resolve(vessel, "engines.0.active").AsBool(), Is.True);
            Assert.That(SnapshotPath.Resolve(vessel, "engines.1.active").AsBool(), Is.False);
            Assert.That(SnapshotPath.Resolve(vessel, "tanks.1.resource").AsString(), Is.EqualTo("monoprop"));
            Assert.That(SnapshotPath.Resolve(vessel, "engines.9.active").IsNull, Is.True, "out of range");
            Assert.That(SnapshotPath.Resolve(vessel, "engines.-1").IsNull, Is.True, "negative index");
            Assert.That(SnapshotPath.Resolve(vessel, "engines.x").IsNull, Is.True, "non-numeric index");
            Assert.That(SnapshotPath.Resolve(vessel, "engines.count").IsNull, Is.True, "lists have no members");
            Assert.That(SnapshotPath.Resolve(vessel, "engines").Kind, Is.EqualTo(ScriptValueKind.List));
        });
    }

    [Test]
    public void RawRecord_UnknownEmptyAndMalformed()
    {
        var vessel = Vessel();
        var whole = SnapshotPath.Resolve(vessel, "");
        Assert.Multiple(() =>
        {
            Assert.That(SnapshotPath.Resolve(vessel, "nope").IsNull, Is.True);
            Assert.That(SnapshotPath.Resolve(vessel, "positionCci.nope").IsNull, Is.True);
            Assert.That(whole.Kind, Is.EqualTo(ScriptValueKind.Map));
            Assert.That(whole["id"].AsString(), Is.EqualTo("test-1"));
            Assert.That(SnapshotPath.Resolve(vessel, null), Is.EqualTo(whole));
            Assert.That(SnapshotPath.Resolve(vessel, "   "), Is.EqualTo(whole));
            Assert.That(SnapshotPath.Resolve(vessel, "a..b").IsNull, Is.True);
            Assert.That(SnapshotPath.Resolve(vessel, ".id").IsNull, Is.True);
            Assert.That(SnapshotPath.Resolve(vessel, "id.").IsNull, Is.True);
            Assert.That(SnapshotPath.Resolve(null, "id").IsNull, Is.True);
        });
    }

    [Test]
    public void RawRecord_NullIntermediate_IsNull()
    {
        var vessel = TestData.Vessel(withOrbit: false);
        Assert.Multiple(() =>
        {
            Assert.That(SnapshotPath.Resolve(vessel, "orbit").IsNull, Is.True);
            Assert.That(SnapshotPath.Resolve(vessel, "orbit.eccentricity").IsNull, Is.True);
        });
    }

    [Test]
    public void LeafValues_AreNotWalked()
    {
        var vessel = Vessel();
        Assert.Multiple(() =>
        {
            Assert.That(SnapshotPath.Resolve(vessel, "name.length").IsNull, Is.True, "strings expose no members");
            Assert.That(SnapshotPath.Resolve(vessel, "positionCci.x.nope").IsNull, Is.True);
        });
    }

    // ---------------------------------------------------------------- script views

    [Test]
    public void ScriptView_ResolvesThroughNestedMaps()
    {
        var view = ScriptViews.Vessel(Vessel());
        Assert.Multiple(() =>
        {
            Assert.That(SnapshotPath.Resolve(view, "position.cci.x").AsNumber(), Is.EqualTo(1234.5));
            Assert.That(SnapshotPath.Resolve(view, "position_cci").IsNull, Is.True, "'position' has no 'cci' sibling at this level");
            Assert.That(SnapshotPath.Resolve(view, "battery.fraction").AsNumber(), Is.EqualTo(0.87));
            Assert.That(SnapshotPath.Resolve(view, "mass.centerOfMass.y").AsNumber(), Is.EqualTo(2));
            Assert.That(SnapshotPath.Resolve(view, "mass.center_of_mass.z").AsNumber(), Is.EqualTo(3), "snake_case matches camelCase");
            Assert.That(SnapshotPath.Resolve(view, "engines.1.ispS").AsNumber(), Is.EqualTo(340));
            Assert.That(SnapshotPath.Resolve(view, "engines.5").IsNull, Is.True);
        });
    }

    [Test]
    public void ScriptValueMapAndList()
    {
        var value = ScriptValue.FromJson("{\"Alpha\":{\"b_c\":[10,20,{\"d\":true}]}}");
        Assert.Multiple(() =>
        {
            Assert.That(SnapshotPath.Resolve(value, "Alpha.b_c.1").AsNumber(), Is.EqualTo(20));
            Assert.That(SnapshotPath.Resolve(value, "alpha.bC.2.d").AsBool(), Is.True, "normalized match");
            Assert.That(SnapshotPath.Resolve(value, "Alpha.b_c.3").IsNull, Is.True);
            Assert.That(SnapshotPath.Resolve(value, "Alpha.b_c.-1").IsNull, Is.True);
            Assert.That(SnapshotPath.Resolve(value, "missing").IsNull, Is.True);
        });
    }

    [Test]
    public void ClrDictionary()
    {
        var dictionary = new Dictionary<string, int> { ["a"] = 1, ["Camel_Case"] = 2 };
        Assert.Multiple(() =>
        {
            Assert.That(SnapshotPath.Resolve(dictionary, "a").AsNumber(), Is.EqualTo(1));
            Assert.That(SnapshotPath.Resolve(dictionary, "camelCase").AsNumber(), Is.EqualTo(2));
            Assert.That(SnapshotPath.Resolve(dictionary, "b").IsNull, Is.True);
        });
    }

    [Test]
    public void ClrListsArraysAndNestedDictionaries()
    {
        var root = new Dictionary<string, object> { ["items"] = new[] { 5, 6, 7 }, ["named"] = new List<string> { "x", "y" } };
        Assert.Multiple(() =>
        {
            Assert.That(SnapshotPath.Resolve(root, "items.2").AsNumber(), Is.EqualTo(7));
            Assert.That(SnapshotPath.Resolve(root, "named.1").AsString(), Is.EqualTo("y"));
            Assert.That(SnapshotPath.Resolve(root, "items.3").IsNull, Is.True);
        });
    }

    [Test]
    public void PropertyLookupIsCachedAndStable()
    {
        var vessel = Vessel();
        for (var i = 0; i < 3; i++)
        {
            Assert.That(SnapshotPath.Resolve(vessel, "latitudeDeg").AsNumber(), Is.EqualTo(45.5));
            Assert.That(SnapshotPath.Resolve(vessel, "doesNotExist").IsNull, Is.True);
        }
    }
}
