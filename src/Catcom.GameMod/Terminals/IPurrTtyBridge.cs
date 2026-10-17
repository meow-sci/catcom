using System.Reflection;
using System.Runtime.Loader;
using Catcom.Core.Logging;

namespace Catcom.GameMod.Terminals;

/// <summary>A snapshot of a purrTTY terminal handle (cheap properties, safe to read from any thread).</summary>
/// <param name="Id">The launcher-assigned id.</param>
/// <param name="Name">The registry name (made unique by purrTTY).</param>
/// <param name="IsOpen">Whether the terminal is still open.</param>
/// <param name="Cols">Current grid columns (0 until measured).</param>
/// <param name="Rows">Current grid rows (0 until measured).</param>
public readonly record struct HandleInfo(Guid Id, string Name, bool IsOpen, int Cols, int Rows);

/// <summary>A program to run in a purrTTY window (the in-game editor): what <see cref="IPurrTtyBridge.LaunchProcessAsync"/> opens.</summary>
/// <param name="Program">The executable.</param>
/// <param name="Arguments">Its command-line arguments.</param>
/// <param name="WorkingDirectory">The working directory.</param>
/// <param name="Environment">Extra environment variables.</param>
/// <param name="Name">The window's registry name.</param>
/// <param name="Title">The window title.</param>
/// <param name="Theme">The purrTTY theme name, or <see langword="null"/> for the default.</param>
/// <param name="Cols">Initial grid columns, or <see langword="null"/> for the default.</param>
/// <param name="Rows">Initial grid rows, or <see langword="null"/> for the default.</param>
/// <param name="Focus">Whether the new window takes input focus.</param>
public sealed record EditorLaunchSpec(
    string Program,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string Name,
    string Title,
    string? Theme,
    int? Cols,
    int? Rows,
    bool Focus);

/// <summary>
///     Everything catcom does with purrTTY, behind types that purrTTY does not define. The implementation
///     (<c>Catcom.PurrTty.PurrTtyBridgeImpl</c>) lives in <c>Catcom.PurrTty.dll</c>, the only assembly that
///     references the purrTTY contract; <see cref="PurrTtyBridge.TryLoad"/> loads it by path once purrTTY is a loaded mod.
///     Handles cross this interface as <see cref="object"/> (an <c>ITerminalHandle</c> in the implementation).
/// </summary>
public interface IPurrTtyBridge
{
    /// <summary>The location of the purrTTY contract assembly as the bridge sees it (diagnoses a split type identity).</summary>
    string ContractLocation { get; }

    /// <summary>True when <c>TerminalLauncherRegistry.Current</c> exists and purrTTY can open terminals now.</summary>
    bool IsLauncherAvailable { get; }

    /// <summary>Subscribes to the launcher becoming available / unavailable (<c>TerminalLauncherRegistry.Changed</c>).</summary>
    /// <param name="onChanged">Called (possibly on any thread) with the new availability.</param>
    /// <returns>A subscription to dispose.</returns>
    IDisposable SubscribeAvailability(Action<bool> onChanged);

    /// <summary>
    ///     Registers catcom's custom shells (<c>ksa-script</c> and one REPL per available runtime). Failures are
    ///     logged per shell; a duplicate id counts as registered.
    /// </summary>
    /// <param name="host">The starting host (read for the runtime availability).</param>
    void RegisterShells(ScriptingHost host);

    /// <summary>
    ///     Requests the terminal for a placement (callable from the game thread; the task completes on another
    ///     thread, once the game rendered a frame).
    /// </summary>
    /// <param name="spec">The placement to open.</param>
    /// <param name="focus">Whether the new terminal takes input focus.</param>
    /// <param name="startScript">Whether attaching may start the script (false when it is already running).</param>
    /// <returns>The launch task; its result is the terminal handle.</returns>
    Task<object> LaunchTerminalAsync(TerminalPlacement spec, bool focus, bool startScript);

    /// <summary>Opens a window running an arbitrary program (the editor).</summary>
    /// <param name="spec">The program and window.</param>
    /// <param name="cancellationToken">Cancels a launch that has not completed.</param>
    /// <returns>The launch task; its result is the terminal handle.</returns>
    Task<object> LaunchProcessAsync(EditorLaunchSpec spec, CancellationToken cancellationToken = default);

    /// <summary>Reads the handle's snapshot properties.</summary>
    /// <param name="handle">A handle returned by a launch.</param>
    /// <returns>The snapshot.</returns>
    HandleInfo Describe(object handle);

    /// <summary>Closes the terminal (completes immediately when already closed).</summary>
    /// <param name="handle">A handle returned by a launch.</param>
    /// <returns>The close task.</returns>
    Task Close(object handle);

    /// <summary>Gives the terminal input focus.</summary>
    /// <param name="handle">A handle returned by a launch.</param>
    /// <returns>The focus task.</returns>
    Task Focus(object handle);

    /// <summary>
    ///     Calls <paramref name="onEnded"/> when the terminal's program exits or the terminal is closed (raised on the
    ///     game's main thread; the callback must only post work).
    /// </summary>
    /// <param name="handle">A handle returned by a launch.</param>
    /// <param name="onEnded">Called when the program exited or the terminal was closed.</param>
    /// <returns>False when the terminal already ended (closed, or its program exited): the callback will not fire.</returns>
    bool Watch(object handle, Action onEnded);
}

/// <summary>
///     Loads and holds the purrTTY bridge. The entry assembly never references purrTTY types: some StarMap
///     builds call <c>GetTypes()</c> on it before the dependency wiring (<c>ImportedAssemblies</c>) exists, and any
///     type whose definition needs a purrTTY type would then resolve the contract through this mod's own deps.json
///     and split the type identity. The bridge assembly is therefore loaded by path, only once purrTTY is a loaded
///     mod, when the wiring is in place and the contract resolves to purrTTY's copy.
/// </summary>
public static class PurrTtyBridge
{
    /// <summary>Why terminals cannot be opened.</summary>
    public const string UnavailableText = "purrTTY launcher unavailable (purrTTY not installed or too old)";

    private const string AssemblyFileName = "Catcom.PurrTty.dll";
    private const string ImplementationTypeName = "Catcom.PurrTty.PurrTtyBridgeImpl";

    private static volatile IPurrTtyBridge? s_current;
    private static volatile string? s_unavailableReason;

    /// <summary>The loaded bridge, or <see langword="null"/> (purrTTY absent, or the bridge could not be loaded).</summary>
    public static IPurrTtyBridge? Current
    {
        get => s_current;
        private set => s_current = value;
    }

    /// <summary>Why <see cref="Current"/> is <see langword="null"/>, for the UI.</summary>
    public static string? UnavailableReason
    {
        get => s_unavailableReason;
        private set => s_unavailableReason = value;
    }

    /// <summary>
    ///     Loads the bridge when purrTTY is a loaded mod and registers the shells. Never throws; on failure
    ///     <see cref="Current"/> stays <see langword="null"/> and <see cref="UnavailableReason"/> says why.
    /// </summary>
    /// <param name="host">The starting host.</param>
    /// <param name="paths">The mod's folders (the bridge assembly sits in the install dir).</param>
    /// <returns>Whether the bridge was loaded.</returns>
    public static bool TryLoad(ScriptingHost host, ModPaths paths)
    {
        Current = null;
        try
        {
            if (!KsaMods.IsLoaded("purrTTY"))
            {
                UnavailableReason = "purrTTY is not installed (terminal surfaces, REPL shells and the in-game editor need it)";
                Log.Info($"{UnavailableReason}.");
                return false;
            }

            var path = Path.Combine(paths.InstallDir, AssemblyFileName);
            if (!File.Exists(path))
            {
                UnavailableReason = $"{AssemblyFileName} is missing from the mod folder ({paths.InstallDir}); reinstall catcom";
                Log.Warn($"purrTTY is installed but {UnavailableReason}.");
                return false;
            }

            // The context that loaded this assembly is the mod's own: purrTTY is a loaded mod, so its dependency wiring
            // (ImportedAssemblies) exists now and the contract references of the bridge resolve to purrTTY's copy.
            var context = AssemblyLoadContext.GetLoadContext(typeof(PurrTtyBridge).Assembly) ?? AssemblyLoadContext.Default;
            var assembly = context.LoadFromAssemblyPath(path);
            var type = assembly.GetType(ImplementationTypeName, throwOnError: true)!;
            var bridge = (IPurrTtyBridge)Activator.CreateInstance(type)!;
            bridge.RegisterShells(host);

            Current = bridge;
            UnavailableReason = null;
            Log.Info($"purrTTY bridge loaded from {assembly.Location}; purrTTY contract assembly: {DescribeContract(bridge)}; launcher available now: {DescribeLauncher(bridge)}.");
            return true;
        }
        catch (Exception ex)
        {
            Current = null;
            UnavailableReason = "purrTTY contract could not be loaded: " + Innermost(ex).Message;
            Log.Warn($"{UnavailableReason} ({ex.GetType().Name}); terminal surfaces, REPL shells and the in-game editor are disabled.\n{ex}");
            return false;
        }
    }

    /// <summary>Drops the bridge (the loaded assembly stays in the mod's load context until the context unloads).</summary>
    public static void Unload()
    {
        Current = null;
        UnavailableReason = "the purrTTY bridge was unloaded";
    }

    private static string DescribeContract(IPurrTtyBridge bridge)
    {
        try
        {
            var location = bridge.ContractLocation;
            return string.IsNullOrEmpty(location) ? "(no location)" : location;
        }
        catch (Exception ex)
        {
            return $"(unknown: {ex.GetType().Name})";
        }
    }

    private static string DescribeLauncher(IPurrTtyBridge bridge)
    {
        try
        {
            return bridge.IsLauncherAvailable ? "yes" : "no";
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static Exception Innermost(Exception exception)
    {
        if (exception is ReflectionTypeLoadException { LoaderExceptions: { } loaders }
            && loaders.FirstOrDefault(e => e is not null) is { } first)
        {
            return Innermost(first);
        }

        while (exception.InnerException is { } inner)
            exception = inner;
        return exception;
    }
}
