using KsaScripting.Core.Config;
using KsaScripting.Core.Scheduling;

namespace KsaScripting.Core.Tests.Scheduling;

[TestFixture]
public sealed class ScheduleConfigTests
{
    [Test]
    public void Default_IsTwoFiveFiftyMillisecondsAndThreeFaults()
    {
        var defaults = ScheduleConfig.Default;

        Assert.Multiple(() =>
        {
            Assert.That(defaults.FrameBudget, Is.EqualTo(TimeSpan.FromMilliseconds(2)));
            Assert.That(defaults.SyncHookBudget, Is.EqualTo(TimeSpan.FromMilliseconds(5)));
            Assert.That(defaults.CallBudget, Is.EqualTo(TimeSpan.FromMilliseconds(50)));
            Assert.That(defaults.FaultLimit, Is.EqualTo(3));
        });
    }

    [Test]
    public void FromConfig_MapsTheTomlValues()
    {
        var cfg = new ScriptingConfig { FrameBudgetMs = 1.5, SyncHookBudgetMs = 7, CallBudgetMs = 80, FaultLimit = 9 };

        var schedule = ScheduleConfig.FromConfig(cfg);

        Assert.Multiple(() =>
        {
            Assert.That(schedule.FrameBudget, Is.EqualTo(TimeSpan.FromMilliseconds(1.5)));
            Assert.That(schedule.SyncHookBudget, Is.EqualTo(TimeSpan.FromMilliseconds(7)));
            Assert.That(schedule.CallBudget, Is.EqualTo(TimeSpan.FromMilliseconds(80)));
            Assert.That(schedule.FaultLimit, Is.EqualTo(9));
        });
    }

    [Test]
    public void FromConfig_OfTheDefaultConfig_EqualsTheDefaultSchedule()
        => Assert.That(ScheduleConfig.FromConfig(new ScriptingConfig()), Is.EqualTo(ScheduleConfig.Default));

    [Test]
    public void StepInfo_ToScriptValue_CarriesUtDtStepIndexAndFrame()
    {
        var map = new StepInfo(12.5, 0.02, 7, 99, true, 0.016).ToScriptValue();

        Assert.Multiple(() =>
        {
            Assert.That(map["ut"].AsNumber(), Is.EqualTo(12.5));
            Assert.That(map["dt"].AsNumber(), Is.EqualTo(0.02));
            Assert.That(map["stepIndex"].AsNumber(), Is.EqualTo(7));
            Assert.That(map["frame"].AsNumber(), Is.EqualTo(99));
            Assert.That(map.AsMap().Count, Is.EqualTo(4));
        });
    }
}
