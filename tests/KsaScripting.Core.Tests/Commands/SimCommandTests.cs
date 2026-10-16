using KsaScripting.Core.Commands;

namespace KsaScripting.Core.Tests.Commands;

[TestFixture]
public sealed class SimCommandTests
{
    private static readonly string[] SolverActions =
    [
        "debug.refill_fuel",
        "debug.refill_battery",
        "debug.teleport",
        "debug.impulse",
        "vessel.attitude_mode",
        "vessel.attitude_frame",
        "vessel.attitude_target",
        "vessel.burn",
        "vessel.rcs_mode",
    ];

    [TestCaseSource(nameof(SolverActions))]
    public void SolverActions_DrainInTheSolverPhase(string action)
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimCommand.PhaseFor(action), Is.EqualTo(CommandPhase.Solver));
            Assert.That(new SimCommand("v1", action, SimCommand.NoOrdinal, 1).Phase, Is.EqualTo(CommandPhase.Solver));
        });
    }

    [TestCase("vessel.throttle")]
    [TestCase("vessel.ignite")]
    [TestCase("camera.focus")]
    [TestCase("engine.active")]
    [TestCase("something.unknown")]
    [TestCase("")]
    public void EverythingElse_IsFramePhase(string action)
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimCommand.PhaseFor(action), Is.EqualTo(CommandPhase.Frame));
            Assert.That(new SimCommand("v1", action, 0, 1).Phase, Is.EqualTo(CommandPhase.Frame));
        });
    }

    [Test]
    public void PhaseLookup_IsCaseSensitive()
        => Assert.That(SimCommand.PhaseFor("DEBUG.REFILL_FUEL"), Is.EqualTo(CommandPhase.Frame));

    [Test]
    public void Payloads_DefaultToNull_AndRideInit()
    {
        var plain = new SimCommand("v1", SimActions.VesselThrottle, SimCommand.NoOrdinal, 0.5);
        var rich = new SimCommand("v1", SimActions.VesselAttitudeTarget, SimCommand.NoOrdinal, 0)
        {
            Values = [1d, 2d, 3d],
            Token = "prograde",
            Aux = "ns",
        };
        Assert.Multiple(() =>
        {
            Assert.That(plain.Values, Is.Null);
            Assert.That(plain.Token, Is.Null);
            Assert.That(plain.Aux, Is.Null);
            Assert.That(rich.Values, Is.EqualTo(new[] { 1d, 2d, 3d }));
            Assert.That(rich.Token, Is.EqualTo("prograde"));
            Assert.That(rich.Aux, Is.EqualTo("ns"));
        });
    }

    [Test]
    public void SimActions_ConstantsMatchTheirKeys()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimActions.VesselThrottle, Is.EqualTo("vessel.throttle"));
            Assert.That(SimActions.VesselBurn, Is.EqualTo("vessel.burn"));
            Assert.That(SimActions.VesselAlwaysRender, Is.EqualTo("vessel.always_render"));
        });
    }

    [Test]
    public void CommandResult_Factories_SetTheOutcomeAndMessage()
    {
        (CommandResult Result, CommandOutcome Expected)[] cases =
        [
            (CommandResult.Invalid("m"), CommandOutcome.Invalid),
            (CommandResult.NotFound("m"), CommandOutcome.NotFound),
            (CommandResult.Denied("m"), CommandOutcome.Denied),
            (CommandResult.Busy("m"), CommandOutcome.Busy),
            (CommandResult.Fault("m"), CommandOutcome.Fault),
            (CommandResult.TimedOut("m"), CommandOutcome.TimedOut),
            (CommandResult.Unsupported("m"), CommandOutcome.Unsupported),
        ];

        Assert.Multiple(() =>
        {
            foreach (var (result, expected) in cases)
            {
                Assert.That(result.Outcome, Is.EqualTo(expected));
                Assert.That(result.Message, Is.EqualTo("m"));
                Assert.That(result.IsSuccess, Is.False);
            }

            Assert.That(CommandResult.Ok.IsSuccess, Is.True);
            Assert.That(CommandResult.Ok.Message, Is.Null);
        });
    }

    [TestCase(CommandOutcome.Ok, "OK")]
    [TestCase(CommandOutcome.Invalid, "EINVAL")]
    [TestCase(CommandOutcome.NotFound, "ENOENT")]
    [TestCase(CommandOutcome.Denied, "EACCES")]
    [TestCase(CommandOutcome.Busy, "EBUSY")]
    [TestCase(CommandOutcome.Fault, "EIO")]
    [TestCase(CommandOutcome.TimedOut, "ETIMEDOUT")]
    [TestCase(CommandOutcome.Unsupported, "EOPNOTSUPP")]
    public void ErrnoName_MapsEveryOutcome(CommandOutcome outcome, string expected)
        => Assert.That(outcome.ErrnoName(), Is.EqualTo(expected));

    [Test]
    public void ValidationRules_AcceptAndReject()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TranslateRules.Validate([1, 0, -1]), Is.Null);
            Assert.That(TranslateRules.Validate([1, 0]), Is.Not.Null);
            Assert.That(RotateRules.Validate([0, double.NaN, 0]), Is.Not.Null);
            Assert.That(ImpulseRules.Validate([1, 2, 3], "body", "dv"), Is.Null);
            Assert.That(ImpulseRules.Validate([1, 2, 3], "xyz", null), Does.Contain("xyz"));
            Assert.That(ScaleRules.IsValid(2), Is.True);
            Assert.That(ScaleRules.IsValid(0), Is.False);
            Assert.That(ScaleRules.IsValid(double.PositiveInfinity), Is.False);
        });
    }
}
