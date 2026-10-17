using Catcom.Core.Snapshots;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Snapshots;

[TestFixture]
public sealed class ScriptViewsTests
{
    private static readonly string[] VesselKeys =
    [
        "id", "name", "situation", "parent", "controlled", "controllable", "isKitten",
        "position", "velocity", "attitude", "altitude", "mass",
        "orbit", "navball", "environment", "power", "battery",
        "engines", "tanks", "rcs", "solar", "generators", "lights", "docking", "decouplers",
        "animations", "srb", "encounters", "parts",
        "throttleCmd", "attitudeMode", "attitudeFrame", "rcsMode", "rcsOn", "engineOn",
        "scale", "alwaysRender", "translateCmd", "rotateCmd",
    ];

    [Test]
    public void Vessel_HasEveryDocumentedKey_InOrder()
    {
        var view = ScriptViews.Vessel(TestData.FullVessel());
        Assert.That(view.AsMap().Keys, Is.EqualTo(VesselKeys));
    }

    [Test]
    public void Vessel_NestedGroupsHaveTheirKeys()
    {
        var view = ScriptViews.Vessel(TestData.FullVessel());
        Assert.Multiple(() =>
        {
            Assert.That(view["position"].AsMap().Keys, Is.EqualTo(new[] { "cci", "ecl", "lat", "lon" }));
            Assert.That(view["velocity"].AsMap().Keys, Is.EqualTo(new[] { "orbital", "surface", "inertial", "cci" }));
            Assert.That(view["attitude"].AsMap().Keys, Is.EqualTo(new[] { "quat", "rates" }));
            Assert.That(view["altitude"].AsMap().Keys, Is.EqualTo(new[] { "barometric", "radar" }));
            Assert.That(view["mass"].AsMap().Keys, Is.EqualTo(new[] { "total", "dry", "propellant", "centerOfMass" }));
            Assert.That(view["power"].AsMap().Keys, Is.EqualTo(new[] { "producedW", "consumedW" }));
            Assert.That(view["battery"].AsMap().Keys, Is.EqualTo(new[] { "fraction", "capacityJ" }));
        });
    }

    [Test]
    public void Vessel_ValuesMatchTheRecord()
    {
        var vessel = TestData.FullVessel() with
        {
            PositionEcl = new double3Snap(4, 5, 6),
            VelocityCci = new double3Snap(7, 8, 9),
            CenterOfMass = new double3Snap(0.1, 0.2, 0.3),
            TranslateCmd = new double3Snap(1, 0, -1),
            RotateCmd = new double3Snap(0, 1, 0),
            Controlled = true,
            ThrottleCmd = 0.25,
        };
        var view = ScriptViews.Vessel(vessel);
        Assert.Multiple(() =>
        {
            Assert.That(view["id"].AsString(), Is.EqualTo("test-1"));
            Assert.That(view["parent"].AsString(), Is.EqualTo("Kerth"));
            Assert.That(view["situation"].AsString(), Is.EqualTo("Freefall"));
            Assert.That(view["controlled"].AsBool(), Is.True);
            Assert.That(view["position"]["cci"], Is.EqualTo(ScriptViews.Vec3(vessel.PositionCci)));
            Assert.That(view["position"]["ecl"], Is.EqualTo(ScriptViews.Vec3(vessel.PositionEcl)));
            Assert.That(view["position"]["lat"].AsNumber(), Is.EqualTo(45.5));
            Assert.That(view["position"]["lon"].AsNumber(), Is.EqualTo(-122.25));
            Assert.That(view["velocity"]["orbital"].AsNumber(), Is.EqualTo(7670.5));
            Assert.That(view["velocity"]["cci"], Is.EqualTo(ScriptViews.Vec3(vessel.VelocityCci)));
            Assert.That(view["attitude"]["quat"], Is.EqualTo(ScriptViews.Quat(vessel.AttitudeBody2Cci)));
            Assert.That(view["attitude"]["rates"], Is.EqualTo(ScriptViews.Vec3(vessel.BodyRatesRadS)));
            Assert.That(view["altitude"]["radar"].AsNumber(), Is.EqualTo(70950.5));
            Assert.That(view["mass"]["propellant"].AsNumber(), Is.EqualTo(8000));
            Assert.That(view["mass"]["centerOfMass"], Is.EqualTo(ScriptViews.Vec3(vessel.CenterOfMass)));
            Assert.That(view["battery"]["fraction"].AsNumber(), Is.EqualTo(0.87));
            Assert.That(view["battery"]["capacityJ"].AsNumber(), Is.EqualTo(9000));
            Assert.That(view["engines"].AsList(), Has.Count.EqualTo(2));
            Assert.That(view["engines"].AsList()[0]["active"].AsBool(), Is.True);
            Assert.That(view["orbit"]["inclinationDeg"].AsNumber(), Is.EqualTo(51.6));
            Assert.That(view["navball"].Kind, Is.EqualTo(ScriptValueKind.Map));
            Assert.That(view["environment"].Kind, Is.EqualTo(ScriptValueKind.Map));
            Assert.That(view["throttleCmd"].AsNumber(), Is.EqualTo(0.25));
            Assert.That(view["rcsMode"].AsString(), Is.EqualTo("Enabled"));
            Assert.That(view["scale"].AsNumber(), Is.EqualTo(1));
            Assert.That(view["translateCmd"]["z"].AsNumber(), Is.EqualTo(-1));
            Assert.That(view["rotateCmd"]["y"].AsNumber(), Is.EqualTo(1));
            Assert.That(view["encounters"].AsList()[0]["body"].AsString(), Is.EqualTo("Mun"));
            Assert.That(view["srb"].AsList(), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Vessel_Battery_IsNullWhenAbsent()
    {
        var vessel = TestData.Vessel(battery: null);
        var view = ScriptViews.Vessel(vessel);
        Assert.Multiple(() =>
        {
            Assert.That(vessel.BatteryCapacityJoules, Is.Null);
            Assert.That(view["battery"]["fraction"].IsNull, Is.True);
            Assert.That(view["battery"]["capacityJ"].IsNull, Is.True);
            Assert.That(view["battery"].AsMap().ContainsKey("fraction"), Is.True, "present with a Null value");
        });
    }

    [Test]
    public void Vessel_OptionalRecords_AreNullWhenAbsent_AndParentDefaultsToEmpty()
    {
        var vessel = TestData.Vessel(withOrbit: false) with { ParentBodyName = null };
        var view = ScriptViews.Vessel(vessel);
        Assert.Multiple(() =>
        {
            Assert.That(view["orbit"].IsNull, Is.True);
            Assert.That(view["navball"].IsNull, Is.True);
            Assert.That(view["environment"].IsNull, Is.True);
            Assert.That(view["parent"].AsString(), Is.EqualTo(""));
        });
    }

    [Test]
    public void Vessel_NonFiniteNumbers_AreZero()
    {
        var vessel = TestData.Vessel() with { MassTotal = double.NaN, CenterOfMass = new double3Snap(double.PositiveInfinity, 0, 0) };
        var view = ScriptViews.Vessel(vessel);
        Assert.Multiple(() =>
        {
            Assert.That(view["mass"]["total"].AsNumber(), Is.Zero);
            Assert.That(view["mass"]["centerOfMass"]["x"].AsNumber(), Is.Zero);
        });
    }

    [Test]
    public void Vec3AndQuat_AreMaps()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptViews.Vec3(new double3Snap(1, 2, 3)).ToJson(), Is.EqualTo("{\"x\":1,\"y\":2,\"z\":3}"));
            Assert.That(ScriptViews.Quat(new QuatSnap(0, 0, 0, 1)).ToJson(), Is.EqualTo("{\"x\":0,\"y\":0,\"z\":0,\"w\":1}"));
        });
    }

    [Test]
    public void System_ProjectsOrIsNull()
    {
        var system = ScriptViews.System(new SystemSnapshot("Kerbol", "Kerth", null));
        Assert.Multiple(() =>
        {
            Assert.That(ScriptViews.System(null).IsNull, Is.True);
            Assert.That(system.AsMap().Keys, Is.EqualTo(new[] { "name", "home", "sun" }));
            Assert.That(system["name"].AsString(), Is.EqualTo("Kerbol"));
            Assert.That(system["home"].AsString(), Is.EqualTo("Kerth"));
            Assert.That(system["sun"].IsNull, Is.True);
        });
    }

    [Test]
    public void Body_IsTheCamelCasedRecord()
    {
        var body = TestData.WithCelestials(SimSnapshot.Empty).Bodies[0];
        var view = ScriptViews.Body(body);
        Assert.Multiple(() =>
        {
            Assert.That(view["id"].AsString(), Is.EqualTo("Kerth"));
            Assert.That(view["meanRadius"].AsNumber(), Is.EqualTo(600000));
            Assert.That(view["childIds"].AsList()[0].AsString(), Is.EqualTo("Mun"));
            Assert.That(view["atmosphere"]["heightM"].AsNumber(), Is.EqualTo(70000));
            Assert.That(view["ocean"]["densityKgM3"].AsNumber(), Is.EqualTo(1000));
        });
    }

    [Test]
    public void Time_HasTheDocumentedKeys()
    {
        var view = ScriptViews.Time(new TimeView(100.5, 4, 0.02, false, [1, 2, 4], true, 500, "v1", 77, 1234));
        Assert.Multiple(() =>
        {
            Assert.That(view.AsMap().Keys, Is.EqualTo(new[]
            {
                "ut", "warp", "simDt", "paused", "warpSpeeds", "autoWarp", "autoWarpTargetUt", "activeVesselId", "frame", "stepIndex",
            }));
            Assert.That(view["ut"].AsNumber(), Is.EqualTo(100.5));
            Assert.That(view["warpSpeeds"].AsList(), Has.Count.EqualTo(3));
            Assert.That(view["activeVesselId"].AsString(), Is.EqualTo("v1"));
            Assert.That(view["frame"].AsNumber(), Is.EqualTo(77));
            Assert.That(view["stepIndex"].AsNumber(), Is.EqualTo(1234));
            Assert.That(ScriptViews.Time(new TimeView(0, 1, 0, true, [], false, 0, null, 0, 0))["activeVesselId"].IsNull, Is.True);
        });
    }
}
