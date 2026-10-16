namespace KsaScripting.Runtime.V8.Tests;

/// <summary>Assembly-wide cleanup of the folders shared by the tests.</summary>
[SetUpFixture]
public sealed class AssemblySetup
{
    [OneTimeTearDown]
    public void DeleteSharedFolders() => V8TestBase.DeleteSharedStubsDir();
}
