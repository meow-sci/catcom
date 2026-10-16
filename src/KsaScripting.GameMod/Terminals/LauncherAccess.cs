using System.Runtime.CompilerServices;
using KsaScripting.GameMod.Shells;
using purrTTY.Core.Terminal;

namespace KsaScripting.GameMod.Terminals;

/// <summary>A snapshot of a purrTTY terminal handle (cheap properties, safe to read from any thread).</summary>
/// <param name="Id">The launcher-assigned id.</param>
/// <param name="Name">The registry name (made unique by purrTTY).</param>
/// <param name="IsOpen">Whether the terminal is still open.</param>
/// <param name="Cols">Current grid columns (0 until measured).</param>
/// <param name="Rows">Current grid rows (0 until measured).</param>
internal readonly record struct HandleInfo(Guid Id, string Name, bool IsOpen, int Cols, int Rows);

/// <summary>
///     The ONLY place that touches the purrTTY launcher types (<c>ITerminalLauncher</c>,
///     <c>ITerminalHandle</c>, <c>TerminalLauncherRegistry</c>, the request POCOs). An older purrTTY exports a
///     contract assembly without them; a method that references a missing type fails to JIT, so every
///     member here is <c>NoInlining</c> with a signature made of BCL types and <see cref="object"/> only,
///     and every caller wraps the call in try/catch. That keeps <see cref="TerminalPlacer"/> itself
///     constructible and harmless on such a purrTTY ("launcher unavailable").
/// </summary>
internal static class LauncherAccess
{
    /// <summary>Why terminals cannot be opened.</summary>
    public const string UnavailableText = "purrTTY launcher unavailable (purrTTY not installed or too old)";

    /// <summary>True when the launcher exists and purrTTY can open terminals now.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsAvailable() => TerminalLauncherRegistry.Current is { IsAvailable: true };

    /// <summary>
    ///     Requests the terminal for a placement (callable from the game thread; the task completes on another
    ///     thread, once the game rendered a frame). The result is the <c>ITerminalHandle</c> as an object.
    /// </summary>
    /// <param name="spec">The placement to open.</param>
    /// <param name="focus">Whether the new terminal takes input focus.</param>
    /// <param name="startScript">Whether attaching may start the script (false when it is already running).</param>
    /// <returns>The launch task.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task<object> LaunchAsync(TerminalPlacement spec, bool focus, bool startScript)
    {
        var launcher = TerminalLauncherRegistry.Current;
        if (launcher is not { IsAvailable: true })
            throw new InvalidOperationException(UnavailableText);

        return Wrap(launcher.LaunchAsync(BuildRequest(spec, focus, startScript)));
    }

    /// <summary>Reads the handle's snapshot properties.</summary>
    /// <param name="handle">An <c>ITerminalHandle</c>.</param>
    /// <returns>The snapshot.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static HandleInfo Describe(object handle)
    {
        var h = (ITerminalHandle)handle;
        return new HandleInfo(h.Id, h.Name, h.IsOpen, h.Cols, h.Rows);
    }

    /// <summary>Closes the terminal (completes immediately when already closed).</summary>
    /// <param name="handle">An <c>ITerminalHandle</c>.</param>
    /// <returns>The close task.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task Close(object handle) => ((ITerminalHandle)handle).CloseAsync();

    /// <summary>Gives the terminal input focus.</summary>
    /// <param name="handle">An <c>ITerminalHandle</c>.</param>
    /// <returns>The focus task.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Task Focus(object handle) => ((ITerminalHandle)handle).FocusAsync();

    /// <summary>
    ///     Subscribes <paramref name="onEnded"/> to the handle's <c>Exited</c> and <c>Closed</c> transitions (raised
    ///     on the game's main thread; the callback must only post work).
    /// </summary>
    /// <param name="handle">An <c>ITerminalHandle</c>.</param>
    /// <param name="onEnded">Called when the program exited or the terminal was closed.</param>
    /// <returns>False when the terminal is already closed (the callback will not fire).</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Watch(object handle, Action onEnded)
    {
        var h = (ITerminalHandle)handle;
        h.Lifecycle += (_, change) =>
        {
            if (change is TerminalLifecycleEvent.Exited or TerminalLifecycleEvent.Closed)
                onEnded();
        };
        return h.IsOpen;
    }

    /// <summary>Subscribes to the launcher becoming available / unavailable (<c>TerminalLauncherRegistry.Changed</c>).</summary>
    /// <param name="onChanged">Called (possibly on any thread) with the new availability.</param>
    /// <returns>A subscription to dispose.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IDisposable SubscribeAvailability(Action<bool> onChanged)
    {
        Action<ITerminalLauncher?> handler = launcher => onChanged(launcher is { IsAvailable: true });
        TerminalLauncherRegistry.Changed += handler;
        return new Subscription(() => TerminalLauncherRegistry.Changed -= handler);
    }

    private static async Task<object> Wrap(Task<ITerminalHandle> launch)
        => await launch.ConfigureAwait(false);

    private static TerminalLaunchRequest BuildRequest(TerminalPlacement spec, bool focus, bool startScript)
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

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
