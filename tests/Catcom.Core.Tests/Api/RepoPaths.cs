namespace Catcom.Core.Tests.Api;

/// <summary>Locates the repository root (the folder holding <c>catcom.slnx</c>) from the test output folder.</summary>
internal static class RepoPaths
{
    internal static string Root { get; } = FindRoot();

    internal static string PreludesDirectory => Path.Combine(Root, "src", "Catcom.Core", "Api", "Preludes");

    internal static string GoldenSourceDirectory => Path.Combine(Root, "tests", "Catcom.Core.Tests", "Golden");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "catcom.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find catcom.slnx above " + TestContext.CurrentContext.TestDirectory);
    }
}
