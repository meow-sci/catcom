namespace KsaScripting.Runtime.V8.Tests;

// Placeholder so `dotnet test` does not report "No test is available" before the V8 runtime tests
// exist. The owning team may delete this file once real tests are added.
[TestFixture]
public sealed class PlaceholderTests
{
    [Test]
    public void CoreAssemblyLoads()
    {
        Assert.That(typeof(KsaScripting.Core.Values.ScriptValue).Assembly.GetName().Name, Is.EqualTo("KsaScripting.Core"));
    }
}
