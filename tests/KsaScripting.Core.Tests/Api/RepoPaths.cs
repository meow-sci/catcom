namespace KsaScripting.Core.Tests.Api;

/// <summary>Locates the repository root (the folder holding <c>ksa-scripting.slnx</c>) from the test output folder.</summary>
internal static class RepoPaths
{
    internal static string Root { get; } = FindRoot();

    internal static string PreludesDirectory => Path.Combine(Root, "src", "KsaScripting.Core", "Api", "Preludes");

    internal static string GoldenSourceDirectory => Path.Combine(Root, "tests", "KsaScripting.Core.Tests", "Golden");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ksa-scripting.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find ksa-scripting.slnx above " + TestContext.CurrentContext.TestDirectory);
    }
}
