using System.Reflection;
using System.Text.Json;
using Catcom.Core.Api;
using Catcom.Core.Commands;
using Catcom.Core.Hooks;
using Catcom.Core.Snapshots;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Api;

[TestFixture]
public sealed class ApiSchemaTests
{
    private static readonly string[] ExpectedClasses =
    [
        "ksa", "Time", "VesselRef", "VesselDebug",
        "EngineRef", "RcsRef", "SolarRef", "LightRef", "DockingRef", "DecouplerRef", "AnimationRef",
        "EngineInfo", "TankInfo", "RcsInfo", "SolarInfo", "GeneratorInfo", "LightInfo", "DockingInfo", "DecouplerInfo",
        "SrbInfo", "SrbSegmentInfo", "EncounterInfo", "PartInfo", "SubpartInfo", "AnimationInfo",
        "OrbitInfo", "NavballInfo", "EnvironmentInfo", "BodyInfo", "AtmosphereInfo", "OceanInfo",
        "Vec3", "Quat", "Position", "Velocity", "Attitude", "Altitude", "Mass", "Power", "Battery",
        "SystemInfo", "CommandResult", "StepInfo", "EventInfo", "Subscription", "Signal", "State", "Console", "ScriptInfo",
        "BaseContext", "EventContext", "TickContext", "ServiceContext", "TerminalContext", "MainContext",
        "Terminal", "Events",
    ];

    private static IEnumerable<(ApiClass Class, ApiMember Member)> AllMembers()
        => ApiSchema.Classes.SelectMany(c => c.Members.Select(m => (Class: c, Member: m)));

    private static HashSet<string> SimActionKeys()
        => typeof(SimActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    [Test]
    public void EveryExpectedClassExists()
    {
        var names = ApiSchema.Classes.Select(c => c.Name).ToList();
        Assert.Multiple(() =>
        {
            foreach (var expected in ExpectedClasses)
                Assert.That(names, Does.Contain(expected), expected);
            Assert.That(names, Is.Unique);
        });
    }

    [Test]
    public void Get_ReturnsTheClassOrThrows()
    {
        Assert.That(ApiSchema.Get("VesselRef").Name, Is.EqualTo("VesselRef"));
        Assert.Throws<ArgumentException>(() => ApiSchema.Get("NoSuchClass"));
    }

    [Test]
    public void EveryExtendsTargetExists()
    {
        var names = ApiSchema.Classes.Select(c => c.Name).ToHashSet();
        foreach (var cls in ApiSchema.Classes.Where(c => c.Extends is not null))
            Assert.That(names, Does.Contain(cls.Extends), $"{cls.Name} extends {cls.Extends}");
    }

    [Test]
    public void EveryActionKeyIsASimActionsConstant()
    {
        var keys = SimActionKeys();
        var actions = AllMembers().Where(x => x.Member.ActionKey is not null).ToList();
        Assert.That(actions, Is.Not.Empty);
        foreach (var (cls, member) in actions)
        {
            Assert.That(keys, Does.Contain(member.ActionKey), $"{cls.Name}.{member.Name}");
            Assert.That(member.Kind, Is.EqualTo(ApiKind.Method), $"{cls.Name}.{member.Name}");
        }
    }

    [Test]
    public void SolverMembers_AreExactlyTheAsyncActionMembers()
    {
        var actions = AllMembers().Where(x => x.Member.ActionKey is not null).ToList();
        Assert.Multiple(() =>
        {
            foreach (var (cls, member) in actions)
            {
                var phase = SimCommand.PhaseFor(member.ActionKey!);
                Assert.That(member.Phase, Is.EqualTo(phase.ToString()), $"{cls.Name}.{member.Name} phase");
                Assert.That(member.Async, Is.EqualTo(phase == CommandPhase.Solver), $"{cls.Name}.{member.Name} async");
            }

            Assert.That(AllMembers().Where(x => x.Member.ActionKey is null).Select(x => x.Member.Phase), Is.All.Null);
        });
    }

    [Test]
    public void ActionMembers_IncludeTheDocumentedSolverSet()
    {
        var solver = AllMembers()
            .Where(x => x.Member is { ActionKey: not null, Async: true })
            .Select(x => x.Member.ActionKey)
            .Order()
            .ToArray();
        Assert.That(solver, Is.EqualTo(new[]
        {
            SimActions.DebugImpulse, SimActions.DebugRefillBattery, SimActions.DebugRefillFuel, SimActions.DebugTeleport,
            SimActions.VesselAttitudeFrame, SimActions.VesselAttitudeMode, SimActions.VesselAttitudeTarget,
            SimActions.VesselBurn, SimActions.VesselRcsMode,
        }.Order()));
    }

    [Test]
    public void VesselRefProperties_EqualTheScriptViewsKeys_AndDebug()
    {
        var viewKeys = ScriptViews.Vessel(TestData.FullVessel()).AsMap().Keys.ToList();
        var props = ApiSchema.Get("VesselRef").Members.Where(m => m.Kind == ApiKind.Property).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(props.Select(m => m.Name).Where(n => n != "debug"), Is.EqualTo(viewKeys));
            Assert.That(props.Select(m => m.Name), Does.Contain("debug"));
            foreach (var key in viewKeys)
                Assert.That(props.Single(m => m.Name == key).ReadPath, Is.EqualTo(key), key);
        });
    }

    [Test]
    public void VesselValueClasses_MatchTheScriptViewsGroups()
    {
        var view = ScriptViews.Vessel(TestData.FullVessel());
        foreach (var (group, className) in new[]
                 {
                     ("position", "Position"), ("velocity", "Velocity"), ("attitude", "Attitude"), ("altitude", "Altitude"),
                     ("mass", "Mass"), ("power", "Power"), ("battery", "Battery"), ("translateCmd", "Vec3"), ("attitude.quat", "Quat"),
                 })
        {
            var map = group == "attitude.quat" ? view["attitude"]["quat"] : view[group];
            var members = ApiSchema.Get(className).Members.Select(m => m.Name);
            Assert.That(members, Is.EqualTo(map.AsMap().Keys), className);
        }
    }

    [Test]
    public void TimeProperties_EqualTheTimeViewKeys()
    {
        var time = new TimeView(1, 1, 0.02, false, [1, 2], false, 0, "v", 5, 6);
        var keys = ScriptValue.FromObject(time).AsMap().Keys.Where(k => k != "activeVesselId").ToList();
        var props = ApiSchema.Get("Time").Members.Where(m => m.Kind == ApiKind.Property).Select(m => m.Name).ToList();
        Assert.That(props, Is.EquivalentTo(keys));
    }

    [Test]
    public void ReflectedInfoClasses_ContainTheCamelCaseNamesOfTheirRecordProperties_InOrder()
    {
        var samples = new Dictionary<string, object>
        {
            ["EngineInfo"] = TestData.FullVessel().Engines.Count > 0 ? TestData.FullVessel().Engines[0] : throw new InvalidOperationException(),
            ["TankInfo"] = TestData.FullVessel().Tanks[0],
            ["RcsInfo"] = TestData.FullVessel().Rcs[0],
            ["SolarInfo"] = TestData.FullVessel().Solar[0],
            ["GeneratorInfo"] = TestData.FullVessel().Generators[0],
            ["LightInfo"] = TestData.FullVessel().Lights[0],
            ["DockingInfo"] = TestData.FullVessel().Docking[0],
            ["DecouplerInfo"] = TestData.FullVessel().Decouplers[0],
            ["SrbInfo"] = TestData.FullVessel().Srb[0],
            ["SrbSegmentInfo"] = TestData.FullVessel().Srb[0].Segments[0],
            ["EncounterInfo"] = TestData.FullVessel().Encounters[0],
            ["AnimationInfo"] = TestData.FullVessel().Animations[0],
            ["OrbitInfo"] = TestData.Vessel().Orbit!,
            ["NavballInfo"] = TestData.FullVessel().Navball!,
            ["EnvironmentInfo"] = TestData.FullVessel().Environment!,
            ["BodyInfo"] = TestData.Snapshot(1, TestData.Vessel()).WithCelestials().Bodies[0],
            ["AtmosphereInfo"] = TestData.Snapshot(1, TestData.Vessel()).WithCelestials().Bodies[0].Atmosphere!,
            ["OceanInfo"] = TestData.Snapshot(1, TestData.Vessel()).WithCelestials().Bodies[0].Ocean!,
        };

        Assert.Multiple(() =>
        {
            foreach (var (name, record) in samples)
            {
                var expected = record.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
                    .ToList();
                var actual = ApiSchema.Get(name).Members.Select(m => m.Name).ToList();
                Assert.That(actual, Is.EqualTo(expected), name);

                // ...and they are exactly the keys the host produces for that record (ScriptValue.FromObject).
                Assert.That(actual, Is.EqualTo(ScriptValue.FromObject(record).AsMap().Keys), name + " host keys");
            }
        });
    }

    [Test]
    public void ReflectedInfoClasses_MapTypesAsDocumented()
    {
        string Ts(string cls, string member) => ApiSchema.Get(cls).Members.Single(m => m.Name == member).Type.TsType;
        string Lua(string cls, string member) => ApiSchema.Get(cls).Members.Single(m => m.Name == member).Type.LuaType;

        Assert.Multiple(() =>
        {
            Assert.That(Ts("EngineInfo", "vacThrustN"), Is.EqualTo("number"));
            Assert.That(Ts("EngineInfo", "active"), Is.EqualTo("boolean"));
            Assert.That(Ts("TankInfo", "resource"), Is.EqualTo("string"));
            Assert.That(Ts("LightInfo", "color"), Is.EqualTo("Vec3"));
            Assert.That(Lua("LightInfo", "color"), Is.EqualTo("ksa.Vec3"));
            Assert.That(Ts("DockingInfo", "dockedToPart"), Is.EqualTo("string | null"));
            Assert.That(Lua("DockingInfo", "dockedToPart"), Is.EqualTo("string?"));
            Assert.That(Ts("SrbInfo", "segments"), Is.EqualTo("SrbSegmentInfo[]"));
            Assert.That(Lua("SrbInfo", "segments"), Is.EqualTo("ksa.SrbSegmentInfo[]"));
            Assert.That(Ts("PartInfo", "subparts"), Is.EqualTo("SubpartInfo[]"));
            Assert.That(Ts("BodyInfo", "childIds"), Is.EqualTo("string[]"));
            Assert.That(Ts("BodyInfo", "orbit"), Is.EqualTo("OrbitInfo | null"));
            Assert.That(Ts("BodyInfo", "atmosphere"), Is.EqualTo("AtmosphereInfo | null"));
            Assert.That(Ts("BodyInfo", "positionEcl"), Is.EqualTo("Vec3"));
        });
    }

    [Test]
    public void RefClasses_ExtendTheirInfoClass()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ApiSchema.Get("EngineRef").Extends, Is.EqualTo("EngineInfo"));
            Assert.That(ApiSchema.Get("RcsRef").Extends, Is.EqualTo("RcsInfo"));
            Assert.That(ApiSchema.Get("SolarRef").Extends, Is.EqualTo("SolarInfo"));
            Assert.That(ApiSchema.Get("LightRef").Extends, Is.EqualTo("LightInfo"));
            Assert.That(ApiSchema.Get("DockingRef").Extends, Is.EqualTo("DockingInfo"));
            Assert.That(ApiSchema.Get("DecouplerRef").Extends, Is.EqualTo("DecouplerInfo"));
            Assert.That(ApiSchema.Get("AnimationRef").Extends, Is.EqualTo("AnimationInfo"));
        });
    }

    [Test]
    public void Events_NamesEqualTheEventCatalog()
    {
        var events = ApiSchema.Get("Events");
        Assert.Multiple(() =>
        {
            Assert.That(events.Members.Select(m => m.Name), Is.EqualTo(EventCatalog.All.Select(e => e.Name)));
            Assert.That(events.Members.Select(m => m.Kind), Is.All.EqualTo(ApiKind.Event));
            foreach (var (member, descriptor) in events.Members.Zip(EventCatalog.All))
            {
                Assert.That(member.Doc, Is.EqualTo(descriptor.Description), member.Name);
                Assert.That(member.Params.Select(p => p.Name), Is.EqualTo(descriptor.PayloadFields), member.Name);
            }
        });
    }

    [Test]
    public void EveryMember_HasDocumentation()
    {
        Assert.Multiple(() =>
        {
            foreach (var cls in ApiSchema.Classes)
            {
                Assert.That(cls.Doc, Is.Not.Empty, cls.Name);
                foreach (var member in cls.Members)
                {
                    Assert.That(member.Doc, Is.Not.Empty, $"{cls.Name}.{member.Name}");
                    if (member.Kind == ApiKind.Method)
                        foreach (var p in member.Params)
                            Assert.That(p.Doc, Is.Not.Empty, $"{cls.Name}.{member.Name}({p.Name})");
                }
            }
        });
    }

    [Test]
    public void MemberNames_AreUniquePerClass_AndLuaNamesDoNotCollide()
    {
        Assert.Multiple(() =>
        {
            foreach (var cls in ApiSchema.Classes)
            {
                Assert.That(cls.Members.Select(m => m.Name), Is.Unique, cls.Name);
                var lua = cls.Members.Where(m => m.Languages != "ts").Select(m => ApiNames.ToSnake(m.Name)).ToList();
                Assert.That(lua, Is.Unique, cls.Name + " (lua)");
            }
        });
    }

    [Test]
    public void SchemaConstruction_IsDeterministic()
    {
        Assert.That(
            StubGenerators.TypeScriptDeclarations(ApiSchema.Classes),
            Is.EqualTo(StubGenerators.TypeScriptDeclarations(ApiSchema.Classes)));
    }

    [TestCase("vacThrustN", "vac_thrust_n")]
    [TestCase("stepIndex", "step_index")]
    [TestCase("positionCci", "position_cci")]
    [TestCase("isKitten", "is_kitten")]
    [TestCase("sunAoaDeg", "sun_aoa_deg")]
    [TestCase("seaLevelDensityKgM3", "sea_level_density_kg_m3")]
    [TestCase("attitudeBody2Cci", "attitude_body2_cci")]
    [TestCase("vessel_id", "vessel_id")]
    [TestCase("already_snake", "already_snake")]
    [TestCase("x", "x")]
    [TestCase("id", "id")]
    [TestCase("", "")]
    public void ToSnake_ConvertsCamelCase(string camel, string snake)
        => Assert.That(ApiNames.ToSnake(camel), Is.EqualTo(snake));
}
