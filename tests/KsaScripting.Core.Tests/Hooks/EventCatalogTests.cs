using KsaScripting.Core.Hooks;

namespace KsaScripting.Core.Tests.Hooks;

[TestFixture]
public sealed class EventCatalogTests
{
    [Test]
    public void Names_AreUnique_AndFindable()
    {
        var names = EventCatalog.All.Select(e => e.Name).ToList();
        Assert.That(names, Is.Unique);
        foreach (var name in names)
            Assert.That(EventCatalog.Find(name)?.Name, Is.EqualTo(name));
        Assert.That(EventCatalog.Find("no.such.event"), Is.Null);
    }

    [Test]
    public void Producers_AreKnown()
    {
        var producers = new[] { "harmony", "differ", "terminal", "host" };
        Assert.That(EventCatalog.All.Select(e => e.Producer), Is.All.AnyOf(producers));
    }

    [Test]
    public void DifferAliases_PointAtCatalogEvents()
    {
        foreach (var (gatos, alias) in EventCatalog.DifferAliases)
        {
            Assert.That(EventCatalog.Find(gatos), Is.Not.Null, gatos);
            Assert.That(EventCatalog.Find(alias), Is.Not.Null, alias);
        }
    }

    [TestCase("engine.activate")]
    [TestCase("situation.change")]
    [TestCase("term.resize")]
    [TestCase("signal.cancel")]
    public void GuideEvents_ArePresent(string name)
        => Assert.That(EventCatalog.Find(name), Is.Not.Null);
}
