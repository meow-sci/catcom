using KsaScripting.Core.Logging;
using KsaScripting.GameMod;
using KsaScripting.GameMod.Shells;
using KsaScripting.GameMod.Terminals;
using KsaScripting.PurrTty.Shells;
using purrTTY.Core.Terminal;

namespace KsaScripting.PurrTty;

/// <summary>
///     The purrTTY side of ksa-scripting: shell registration and the launcher (terminal surfaces, the editor
///     window). This assembly is the ONLY one that references the purrTTY contract types; it is loaded by path by
///     <see cref="PurrTtyBridge.TryLoad"/> once purrTTY is a loaded mod, so the contract resolves to purrTTY's copy.
///     Handles cross the <see cref="IPurrTtyBridge"/> boundary as <see cref="object"/> (an <c>ITerminalHandle</c>).
/// </summary>
/// <remarks>
///     An older purrTTY exports a contract assembly without the launcher types: a member that references a missing
///     type fails when it is first called (the JIT cannot compile it), never at construction, so every caller
///     wraps the calls in try/catch and the shells still register on such a purrTTY.
/// </remarks>
public sealed class PurrTtyBridgeImpl : IPurrTtyBridge
{
    /// <inheritdoc />
    public string ContractLocation => typeof(CustomShellRegistry).Assembly.Location;

    /// <inheritdoc />
    public bool IsLauncherAvailable => TerminalLauncherRegistry.Current is { IsAvailable: true };

    /// <inheritdoc />
    public IDisposable SubscribeAvailability(Action<bool> onChanged)
    {
        Action<ITerminalLauncher?> handler = launcher => onChanged(launcher is { IsAvailable: true });
        TerminalLauncherRegistry.Changed += handler;
        return new Subscription(() => TerminalLauncherRegistry.Changed -= handler);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Registration cannot be undone (the registry has no unregister), so the shell factories reach the running
    ///     host through <see cref="ScriptingHost.Current"/> and tolerate it being null after an unload.
    /// </remarks>
    public void RegisterShells(ScriptingHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

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

        if (registered.Count > 0)
            Log.Info($"Registered purrTTY shells: {string.Join(", ", registered)}.");
    }

    /// <inheritdoc />
    public Task<object> LaunchTerminalAsync(TerminalPlacement spec, bool focus, bool startScript)
    {
        var launcher = TerminalLauncherRegistry.Current;
        if (launcher is not { IsAvailable: true })
            throw new InvalidOperationException(PurrTtyBridge.UnavailableText);

        return Wrap(launcher.LaunchAsync(BuildTerminalRequest(spec, focus, startScript)));
    }

    /// <inheritdoc />
    public Task<object> LaunchProcessAsync(EditorLaunchSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var launcher = TerminalLauncherRegistry.Current;
        if (launcher is not { IsAvailable: true })
            throw new InvalidOperationException(PurrTtyBridge.UnavailableText);

        return Wrap(launcher.LaunchAsync(BuildProcessRequest(spec), cancellationToken));
    }

    /// <inheritdoc />
    public HandleInfo Describe(object handle)
    {
        var h = (ITerminalHandle)handle;
        return new HandleInfo(h.Id, h.Name, h.IsOpen, h.Cols, h.Rows);
    }

    /// <inheritdoc />
    public Task Close(object handle) => ((ITerminalHandle)handle).CloseAsync();

    /// <inheritdoc />
    public Task Focus(object handle) => ((ITerminalHandle)handle).FocusAsync();

    /// <inheritdoc />
    public bool Watch(object handle, Action onEnded)
    {
        var h = (ITerminalHandle)handle;
        h.Lifecycle += (_, change) =>
        {
            if (change is TerminalLifecycleEvent.Exited or TerminalLifecycleEvent.Closed)
                onEnded();
        };

        // Subscribed first, so an end between the subscription and this read is reported twice rather than never.
        return h.IsOpen && h.ExitCode is null;
    }

    private static async Task<object> Wrap(Task<ITerminalHandle> launch)
        => await launch.ConfigureAwait(false);

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

    private static TerminalLaunchRequest BuildTerminalRequest(TerminalPlacement spec, bool focus, bool startScript)
    {
        var program = new TerminalProgramSpec { CustomShellId = ScriptTerminalShell.ShellId };
        program.Environment[ScriptTerminalShell.ScriptVariable] = spec.Script;
        program.Environment[ScriptTerminalShell.AutostartVariable] = startScript ? "1" : "0";

        var name = "ksa:" + spec.Script;
        if (spec.InWorld is { } world)
        {
            return new TerminalLaunchRequest
            {
                Placement = TerminalPlacementKind.InWorldPart,
                Program = program,
                InWorld = new TerminalInWorldOptions
                {
                    Name = name,
                    ThemeName = spec.Theme,
                    Cols = spec.Cols,
                    Rows = spec.Rows,
                    VehicleId = spec.VehicleId ?? "",
                    PartInstanceId = spec.PartInstanceId,
                    OffsetX = world.OffsetX,
                    OffsetY = world.OffsetY,
                    OffsetZ = world.OffsetZ,
                    RotationX = world.RotationX,
                    RotationY = world.RotationY,
                    RotationZ = world.RotationZ,
                    WidthMeters = world.WidthMeters,
                    HeightMeters = world.HeightMeters,
                    BackgroundOpacity = world.BackgroundOpacity,
                    Focus = focus,
                },
            };
        }

        return new TerminalLaunchRequest
        {
            Placement = TerminalPlacementKind.Window,
            Program = program,
            Window = new TerminalWindowOptions
            {
                Name = name,
                Title = spec.Title ?? spec.Script,
                ThemeName = spec.Theme,
                Cols = spec.Cols,
                Rows = spec.Rows,
                Focus = focus,
            },
        };
    }

    private static TerminalLaunchRequest BuildProcessRequest(EditorLaunchSpec spec)
    {
        var program = new TerminalProgramSpec
        {
            Program = spec.Program,
            WorkingDirectory = spec.WorkingDirectory,
        };
        program.Arguments.AddRange(spec.Arguments);
        foreach (var (key, value) in spec.Environment)
            program.Environment[key] = value;

        return new TerminalLaunchRequest
        {
            Placement = TerminalPlacementKind.Window,
            Program = program,
            Window = new TerminalWindowOptions
            {
                Name = spec.Name,
                Title = spec.Title,
                ThemeName = spec.Theme,
                Cols = spec.Cols,
                Rows = spec.Rows,
                Focus = spec.Focus,
            },
        };
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
