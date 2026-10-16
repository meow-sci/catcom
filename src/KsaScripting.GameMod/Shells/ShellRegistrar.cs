using System.Runtime.CompilerServices;
using KsaScripting.Core.Logging;
using purrTTY.Core.Terminal;

namespace KsaScripting.GameMod.Shells;

/// <summary>
///     Registers ksa-scripting's custom shells with purrTTY's <see cref="CustomShellRegistry"/>: the
///     <c>ksa-script</c> terminal shell and one REPL per available runtime. Registration cannot be undone
///     (the registry has no unregister), so the shell factories reach the running host through
///     <see cref="ScriptingHost.Current"/> and tolerate it being null after an unload.
/// </summary>
internal static class ShellRegistrar
{
    /// <summary>
    ///     Registers the shells; failures are logged per shell (a duplicate id counts as registered). The call is
    ///     <c>NoInlining</c> so a purrTTY contract that cannot be loaded surfaces as an exception at the call site.
    /// </summary>
    /// <param name="host">The starting host (read for the runtime availability).</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Register(ScriptingHost host)
    {
        var registered = new List<string>();

        if (TryRegister(ScriptTerminalShell.ShellId, () => new ScriptTerminalShell()))
            registered.Add(ScriptTerminalShell.ShellId);

        foreach (var status in host.RuntimeStatuses)
        {
            if (!status.Available || ReplFlavor.ForRuntime(status.Id) is not { } flavor)
                continue;

            if (TryRegister(flavor.ShellId, () => new ScriptReplShell(flavor)))
                registered.Add(flavor.ShellId);
        }

        if (registered.Count == 0)
            return;

        if (UsesOwnContractCopy())
            Log.Info("purrTTY not installed: shells registered into a registry nobody reads.");
        else
            Log.Info($"Registered purrTTY shells: {string.Join(", ", registered)}.");
    }

    private static bool TryRegister<T>(string shellId, Func<T> factory)
        where T : class, ICustomShell
    {
        try
        {
            if (CustomShellRegistry.Instance.IsShellRegistered(shellId))
                return true; // an earlier start of this mod registered it; the factory is host-independent

            // The registry probe-instantiates (and disposes) one instance to validate Metadata: the
            // constructors are trivial by design.
            CustomShellRegistry.Instance.RegisterShell(shellId, factory);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not register the '{shellId}' shell with purrTTY: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    ///     True when the contract assembly loaded from our own folder, i.e. purrTTY is absent and nothing
    ///     enumerates the registry (the gatOS detection).
    /// </summary>
    private static bool UsesOwnContractCopy()
    {
        try
        {
            var contractDir = DirectoryOf(typeof(CustomShellRegistry));
            var ownDir = DirectoryOf(typeof(ShellRegistrar));
            if (contractDir is null || ownDir is null)
                return false;

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(contractDir),
                Path.TrimEndingDirectorySeparator(ownDir),
                comparison);
        }
        catch
        {
            return false;
        }
    }

    private static string? DirectoryOf(Type type)
    {
        try
        {
            var location = type.Assembly.Location;
            return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        }
        catch
        {
            return null; // dynamic / byte-loaded assemblies have no location
        }
    }
}
