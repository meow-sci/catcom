using Catcom.Core.Commands;
using Catcom.Core.Hosting;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Hosting;

[TestFixture]
public sealed class CommandResultExtensionsTests
{
    [Test]
    public void Ok_IsTrueWithNullMessage()
    {
        var value = CommandResult.Ok.ToScriptValue();
        Assert.Multiple(() =>
        {
            Assert.That(value.AsMap().Keys, Is.EqualTo(new[] { "ok", "outcome", "message" }));
            Assert.That(value["ok"].AsBool(), Is.True);
            Assert.That(value["outcome"].AsString(), Is.EqualTo("Ok"));
            Assert.That(value["message"].IsNull, Is.True);
        });
    }

    [Test]
    public void Failure_CarriesTheOutcomeNameAndMessage()
    {
        var value = CommandResult.Denied("control is disabled").ToScriptValue();
        Assert.Multiple(() =>
        {
            Assert.That(value["ok"].AsBool(), Is.False);
            Assert.That(value["outcome"].AsString(), Is.EqualTo("Denied"));
            Assert.That(value["message"].AsString(), Is.EqualTo("control is disabled"));
            Assert.That(value.ToJson(), Is.EqualTo("{\"ok\":false,\"outcome\":\"Denied\",\"message\":\"control is disabled\"}"));
        });
    }

    [Test]
    public void EveryOutcome_ProducesItsEnumName()
    {
        foreach (var outcome in Enum.GetValues<CommandOutcome>())
        {
            var value = new CommandResult(outcome).ToScriptValue();
            Assert.That(value["outcome"].AsString(), Is.EqualTo(outcome.ToString()));
        }
    }
}
