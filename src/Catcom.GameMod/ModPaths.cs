using System.Runtime.InteropServices;
using Catcom.Core.Logging;
using Catcom.Ksa;

namespace Catcom.GameMod;

/// <summary>
///     Every file-system location the mod uses (guide §0/§7). The install dir is where the mod's
///     DLLs live (the dist folder); the data dir is the user-writable folder under
///     <c>&lt;KSA documents folder&gt;/mods/catcom</c> (normally
///     <c>&lt;Documents&gt;/My Games/Kitten Space Agency/mods/catcom</c>; on Windows the two are
///     normally the same folder). Pure path math — nothing here touches the disk except
///     <see cref="EnsureDataDirs"/>.
/// </summary>
public sealed class ModPaths
{
    /// <summary>The mod's folder name (kebab case, matches the dist folder).</summary>
    public const string ModFolderName = "catcom";

    /// <summary>Creates the paths for an explicit install dir and data dir (tests, tools).</summary>
    public ModPaths(string installDir, string dataDir)
    {
        InstallDir = Path.GetFullPath(installDir);
        DataDir = Path.GetFullPath(dataDir);
    }

    /// <summary>
    ///     The production layout: install dir = the folder containing this assembly; data dir =
    ///     <c>&lt;KSA documents folder&gt;/mods/catcom</c>. The documents folder is the game's own
    ///     <c>Constants.DocumentsFolderPath</c> (<c>&lt;MyDocuments&gt;/My Games/Kitten Space Agency</c> unless the
    ///     StarMap loader relocated it with <c>-InstancePath</c> / <c>STARMAP_INSTANCE_PATH</c>), so the mod's data
    ///     follows the instance the game actually runs in.
    /// </summary>
    [KsaAnchor("Constants.DocumentsFolderPath", SourceFile = "KSA/Constants.cs", Verified = "2026-10-22",
        GameVersion = "2026.10.19.5638", Risk = ChurnRisk.Low,
        Notes = "The folder KSA keeps settings.toml, logs and mods/ in. StarMap can override it (-InstancePath / STARMAP_INSTANCE_PATH). "
            + "Falls back to <MyDocuments>/My Games/Kitten Space Agency when reading it throws."
            + " 5638: Constants.cs:283, unchanged.")]
    public static ModPaths Detect()
    {
        var install = Path.GetDirectoryName(typeof(ModPaths).Assembly.Location) ?? AppContext.BaseDirectory;

        string? documents = null;
        try
        {
            documents = KSA.Constants.DocumentsFolderPath;
        }
        catch (Exception ex)
        {
            Log.LogOnce("paths.documents", LogLevel.Warn, $"Reading the game's documents folder failed; using <MyDocuments>/My Games/Kitten Space Agency: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(documents))
            documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Kitten Space Agency");

        return new ModPaths(install, Path.Combine(documents, "mods", ModFolderName));
    }

    /// <summary>The folder holding the mod's DLLs, natives, editor blobs and shipped libraries.</summary>
    public string InstallDir { get; }

    /// <summary>The user-writable data folder (scripts, bindings, config, editor state).</summary>
    public string DataDir { get; }

    /// <summary><c>&lt;data&gt;/catcom.toml</c>.</summary>
    public string ConfigFile => Path.Combine(DataDir, "catcom.toml");

    /// <summary><c>&lt;data&gt;/bindings.toml</c> (UI-created bindings).</summary>
    public string BindingsFile => Path.Combine(DataDir, "bindings.toml");

    /// <summary><c>&lt;data&gt;/terminals.toml</c> (terminal placements).</summary>
    public string TerminalsFile => Path.Combine(DataDir, "terminals.toml");

    /// <summary><c>&lt;data&gt;/scripts</c> — the user's scripts.</summary>
    public string ScriptsDir => Path.Combine(DataDir, "scripts");

    /// <summary><c>&lt;data&gt;/scripts/lib</c> — shipped helper libraries, overwritten on every start.</summary>
    public string ScriptsLibDir => Path.Combine(ScriptsDir, "lib");

    /// <summary><c>&lt;data&gt;/scripts/stubs</c> — generated type stubs.</summary>
    public string StubsDir => Path.Combine(ScriptsDir, "stubs");

    /// <summary><c>&lt;install&gt;/scripts-lib</c> — the shipped copy of the helper libraries.</summary>
    public string ShippedScriptsLibDir => Path.Combine(InstallDir, "scripts-lib");

    /// <summary><c>&lt;data&gt;/editor</c> — nvim data/state/cache roots (XDG_*_HOME).</summary>
    public string EditorDataRoot => Path.Combine(DataDir, "editor");

    /// <summary><c>&lt;data&gt;/editor/data</c> (XDG_DATA_HOME).</summary>
    public string EditorDataHome => Path.Combine(EditorDataRoot, "data");

    /// <summary><c>&lt;data&gt;/editor/state</c> (XDG_STATE_HOME).</summary>
    public string EditorStateHome => Path.Combine(EditorDataRoot, "state");

    /// <summary><c>&lt;data&gt;/editor/cache</c> (XDG_CACHE_HOME).</summary>
    public string EditorCacheHome => Path.Combine(EditorDataRoot, "cache");

    /// <summary><c>&lt;install&gt;/editor/config</c> (XDG_CONFIG_HOME; holds ksa-editor/init.lua).</summary>
    public string EditorConfigHome => Path.Combine(InstallDir, "editor", "config");

    /// <summary><c>&lt;install&gt;/editor/config/ksa-editor/init.lua</c>.</summary>
    public string EditorInitLua => Path.Combine(EditorConfigHome, "ksa-editor", "init.lua");

    /// <summary><c>&lt;install&gt;/editor/noshell</c> (or <c>noshell.cmd</c> on Windows).</summary>
    public string NoShell => Path.Combine(InstallDir, "editor", OperatingSystem.IsWindows() ? "noshell.cmd" : "noshell");

    /// <summary>The .NET runtime identifier of this process: win-x64, linux-x64, osx-arm64, …</summary>
    public static string Rid
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                var other => other.ToString().ToLowerInvariant(),
            };
            return $"{os}-{arch}";
        }
    }

    /// <summary><c>&lt;install&gt;/editor/&lt;rid&gt;</c> — fetched nvim + LSP blobs for this platform.</summary>
    public string EditorRidDir => Path.Combine(InstallDir, "editor", Rid);

    /// <summary>The shipped nvim executable for this platform.</summary>
    public string NvimExecutable => Path.Combine(EditorRidDir, "nvim", "bin", OperatingSystem.IsWindows() ? "nvim.exe" : "nvim");

    /// <summary><c>&lt;install&gt;/editor/&lt;rid&gt;/lsp</c> (KSA_LSP_DIR).</summary>
    public string LspDir => Path.Combine(EditorRidDir, "lsp");

    /// <summary>The V8 code-cache / compiler cache folder under the data dir.</summary>
    public string CacheDir => Path.Combine(DataDir, "cache");

    /// <summary>Resolves a script name (relative path with '/') to its full path under <see cref="ScriptsDir"/>.</summary>
    public string ScriptPath(string scriptName)
        => Path.GetFullPath(Path.Combine(ScriptsDir, scriptName.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Creates the data, scripts, lib, stubs and editor folders when missing. Never throws.</summary>
    public IReadOnlyList<string> EnsureDataDirs()
    {
        var failures = new List<string>();
        foreach (var dir in new[] { DataDir, ScriptsDir, ScriptsLibDir, StubsDir, EditorDataHome, EditorStateHome, EditorCacheHome, CacheDir })
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                failures.Add($"{dir}: {ex.Message}");
            }
        }

        return failures;
    }
}
