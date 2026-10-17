namespace Catcom.Runtime.V8;

/// <summary>Locates the downloaded transpiler assets (<c>typescript.js</c>, <c>sucrase.bundle.js</c>) next to the assembly.</summary>
internal static class V8Assets
{
    /// <summary>The environment variable that overrides the asset directory.</summary>
    public const string DirectoryEnvironmentVariable = "KSA_V8_ASSETS_DIR";

    /// <summary>The file name of the TypeScript compiler bundle.</summary>
    public const string TypeScriptFileName = "typescript.js";

    /// <summary>The file name of the sucrase fallback bundle.</summary>
    public const string SucraseFileName = "sucrase.bundle.js";

    /// <summary>The directory holding this assembly (falls back to the app base directory).</summary>
    public static string AssemblyDirectory
    {
        get
        {
            var location = typeof(V8Assets).Assembly.Location;
            var directory = string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
            return string.IsNullOrEmpty(directory) ? AppContext.BaseDirectory : directory;
        }
    }

    /// <summary>The asset directory: <c>$KSA_V8_ASSETS_DIR</c> when set, otherwise <c>&lt;assembly dir&gt;/assets</c>.</summary>
    public static string Directory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(AssemblyDirectory, "assets")
                : Path.GetFullPath(overridePath);
        }
    }

    /// <summary>The full path of <c>typescript.js</c>.</summary>
    public static string TypeScriptPath => Path.Combine(Directory, TypeScriptFileName);

    /// <summary>The full path of <c>sucrase.bundle.js</c>.</summary>
    public static string SucrasePath => Path.Combine(Directory, SucraseFileName);

    /// <summary>Whether at least one transpiler bundle is present.</summary>
    public static bool HasTranspiler => File.Exists(TypeScriptPath) || File.Exists(SucrasePath);
}
