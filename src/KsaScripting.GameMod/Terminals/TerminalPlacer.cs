using KsaScripting.Core.Config;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Scheduling;
using KsaScripting.Core.Terminals;

namespace KsaScripting.GameMod.Terminals;

/// <summary>Options for putting a script terminal on a vehicle part (defaults: 40×12 cells, ≈1.69 aspect quad).</summary>
public sealed record InWorldPlacementOptions(
    int Cols = 40, int Rows = 12,
    float OffsetX = 0f, float OffsetY = 0f, float OffsetZ = 0.05f,
    float RotationX = 0f, float RotationY = 0f, float RotationZ = 0f,
    float WidthMeters = 0.6f, float HeightMeters = 0.355f,
    string? Theme = null, float? BackgroundOpacity = null);

/// <summary>A persisted placement (one <c>[[placement]]</c> row of terminals.toml). Kind: "window" | "inworld".</summary>
public sealed record TerminalPlacement(
    string Script, string Kind, int Cols, int Rows, string? Title, string? Theme,
    string? VehicleId, uint? PartInstanceId, InWorldPlacementOptions? InWorld);

/// <summary>A live purrTTY surface attached to a script terminal.</summary>
public sealed record PlacedTerminal(Guid Id, string Script, string Kind, string Name, bool IsOpen, int Cols, int Rows);

/// <summary>
///     Opens purrTTY surfaces (2D windows, in-world part quads) for script terminals through the
///     purrTTY launcher contract (<c>TerminalLauncherRegistry.Current</c>), persists placements in
///     terminals.toml and re-creates them when the script starts. Degrades to "purrTTY launcher
///     unavailable" when purrTTY (or its launcher) is absent. Game thread only, except where noted.
/// </summary>
/// <remarks>
///     Placements are re-created by a reconcile pass: for every persisted placement of a running script
///     that has no live (or in-flight) surface, the launcher is available and — for in-world placements —
///     the vehicle exists. The pass runs when a script starts, when the launcher becomes available and
///     (via <see cref="RequestReconcile"/>) when the host sees vessels or a save appear. A placement whose
///     surface was closed by the user, exited by itself or failed to open is not re-created until its
///     script starts again. All launcher types are reached through <see cref="LauncherAccess"/>.
/// </remarks>
public sealed class TerminalPlacer : IDisposable
{
    private readonly GameThreadQueue _queue;
    private readonly PlacementStore _store;
    private readonly List<TerminalPlacement> _placements;
    private readonly List<Surface> _surfaces = [];
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly HashSet<TerminalPlacement> _suppressed = [];
    private IDisposable? _availabilitySubscription;
    private volatile bool _disposed;
    private int _reconcileQueued;

    /// <summary>Creates the placer.</summary>
    public TerminalPlacer(ModPaths paths, ScriptingConfig config, TerminalRegistry terminals, GameThreadQueue queue)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(queue);
        _ = config;
        _ = terminals;

        _queue = queue;
        _store = new PlacementStore(paths.TerminalsFile);
        _placements = _store.Load();

        try
        {
            _availabilitySubscription = LauncherAccess.SubscribeAvailability(OnAvailabilityChanged);
        }
        catch (Exception ex)
        {
            // An older purrTTY without the launcher contract: the placer simply stays unavailable.
            Log.LogOnce("placer.subscribe", LogLevel.Info, $"The purrTTY launcher registry is not available ({ex.GetType().Name}); terminal surfaces are disabled.");
        }
    }

    /// <summary>True when the purrTTY launcher is present and available.</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                return LauncherAccess.IsAvailable();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Why <see cref="IsAvailable"/> is false.</summary>
    public string? UnavailableReason => IsAvailable ? null : LauncherAccess.UnavailableText;

    /// <summary>
    ///     Supplies the ids of the vehicles that exist right now (the bridge's vessel list); in-world
    ///     placements are only re-created when their vehicle is in it. Unset means "cannot tell".
    /// </summary>
    internal Func<IReadOnlyCollection<string>>? VesselIds { get; set; }

    /// <summary>Persisted placements of a script.</summary>
    public IReadOnlyList<TerminalPlacement> Placements(string script)
        => _placements.Where(p => string.Equals(p.Script, script, StringComparison.Ordinal)).ToArray();

    /// <summary>Live surfaces of a script.</summary>
    public IReadOnlyList<PlacedTerminal> Open(string script)
    {
        var result = new List<PlacedTerminal>();
        foreach (var surface in _surfaces.ToArray())
        {
            if (surface.Handle is null || !string.Equals(surface.Spec.Script, script, StringComparison.Ordinal))
                continue;

            try
            {
                var info = LauncherAccess.Describe(surface.Handle);
                result.Add(new PlacedTerminal(info.Id, surface.Spec.Script, surface.Spec.Kind, info.Name, info.IsOpen, info.Cols, info.Rows));
            }
            catch (Exception ex)
            {
                Log.LogOnce("placer.describe", LogLevel.Warn, $"Reading a terminal handle failed: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>Opens a 2D purrTTY window on the script's terminal.</summary>
    public void OpenWindow(string script, int cols, int rows, string? title, string? theme, bool persist)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(script))
                return;

            var spec = new TerminalPlacement(
                script, PlacementKinds.Window, Math.Clamp(cols, 1, 1000), Math.Clamp(rows, 1, 1000),
                NullIfBlank(title), NullIfBlank(theme), null, null, null);
            Place(spec, persist, focus: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening a terminal window for '{script}' failed: {ex.Message}");
        }
    }

    /// <summary>Opens an in-world terminal on a vehicle part addressed by <c>Part.InstanceId</c>.</summary>
    public void PlaceOnPart(string script, string vehicleId, uint partInstanceId, InWorldPlacementOptions options, bool persist)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(script))
                return;

            options ??= new InWorldPlacementOptions();
            var spec = new TerminalPlacement(
                script, PlacementKinds.InWorld, options.Cols, options.Rows, null, NullIfBlank(options.Theme),
                vehicleId ?? "", partInstanceId, options with { Theme = NullIfBlank(options.Theme) });
            Place(spec, persist, focus: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Placing a terminal for '{script}' on a part failed: {ex.Message}");
        }
    }

    /// <summary>Closes a live surface.</summary>
    public void Close(Guid id)
    {
        try
        {
            var surface = FindSurface(id);
            if (surface is null)
                return;

            // Closed on request: do not bring it back until the script restarts.
            _surfaces.Remove(surface);
            surface.Cancelled = true;
            _suppressed.Add(surface.Spec);
            CloseHandle(surface.Handle);
        }
        catch (Exception ex)
        {
            Log.Warn($"Closing a terminal failed: {ex.Message}");
        }
    }

    /// <summary>Focuses a live surface.</summary>
    public void Focus(Guid id)
    {
        try
        {
            var surface = FindSurface(id);
            if (surface?.Handle is null)
                return;

            ObserveFaults(LauncherAccess.Focus(surface.Handle), "Focusing a terminal");
        }
        catch (Exception ex)
        {
            Log.Warn($"Focusing a terminal failed: {ex.Message}");
        }
    }

    /// <summary>Removes a persisted placement by its index in <see cref="Placements"/>.</summary>
    public void RemovePlacement(string script, int index)
    {
        try
        {
            var mine = Placements(script);
            if (index < 0 || index >= mine.Count)
                return;

            _placements.Remove(mine[index]);
            _suppressed.Remove(mine[index]);
            SavePlacements();
        }
        catch (Exception ex)
        {
            Log.Warn($"Removing a terminal placement of '{script}' failed: {ex.Message}");
        }
    }

    /// <summary>Re-creates persisted placements of a script that just started (in-world ones only when the vehicle exists).</summary>
    public void OnScriptStarted(string script)
    {
        try
        {
            if (_disposed || string.IsNullOrEmpty(script))
                return;

            _running.Add(script);
            _suppressed.RemoveWhere(p => string.Equals(p.Script, script, StringComparison.Ordinal));
            Reconcile(script);
        }
        catch (Exception ex)
        {
            Log.LogOnce("placer.started", LogLevel.Warn, $"Re-creating the terminals of '{script}' failed: {ex.Message}");
        }
    }

    /// <summary>Closes the live surfaces of a script that stopped.</summary>
    public void OnScriptStopped(string script)
    {
        try
        {
            _running.Remove(script);
            foreach (var surface in _surfaces.Where(s => string.Equals(s.Spec.Script, script, StringComparison.Ordinal)).ToArray())
            {
                _surfaces.Remove(surface);
                surface.Cancelled = true;
                CloseHandle(surface.Handle);
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("placer.stopped", LogLevel.Warn, $"Closing the terminals of '{script}' failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Asks for a reconcile pass on the game thread (coalesced; callable from any thread): the host calls
    ///     it when vessels or a save appear so in-world placements whose vehicle was missing get created.
    /// </summary>
    internal void RequestReconcile()
    {
        if (_disposed || Interlocked.Exchange(ref _reconcileQueued, 1) == 1)
            return;

        _queue.PostWork(() =>
        {
            Volatile.Write(ref _reconcileQueued, 0);
            try
            {
                Reconcile(null);
            }
            catch (Exception ex)
            {
                Log.LogOnce("placer.reconcile", LogLevel.Warn, $"Re-creating terminals failed: {ex.Message}");
            }
        });
    }

    /// <summary>Closes everything.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _availabilitySubscription?.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogOnce("placer.unsubscribe", LogLevel.Warn, $"Unsubscribing from the launcher registry failed: {ex.Message}");
        }

        _availabilitySubscription = null;
        foreach (var surface in _surfaces.ToArray())
        {
            surface.Cancelled = true;
            CloseHandle(surface.Handle);
        }

        _surfaces.Clear();
        _running.Clear();
    }

    // ---------------------------------------------------------------- opening

    private void Place(TerminalPlacement spec, bool persist, bool focus)
    {
        if (persist && !_placements.Contains(spec))
        {
            _placements.Add(spec);
            SavePlacements();
        }

        _suppressed.Remove(spec);
        Launch(spec, focus, userRequested: true);
    }

    private void Launch(TerminalPlacement spec, bool focus, bool userRequested)
    {
        if (_disposed)
            return;

        if (!IsAvailable)
        {
            if (userRequested)
                Log.Warn($"Cannot open a terminal for '{spec.Script}': {LauncherAccess.UnavailableText}.");
            return;
        }

        var surface = new Surface(spec);
        Task<object> launch;
        try
        {
            launch = LauncherAccess.LaunchAsync(spec, focus, startScript: userRequested);
        }
        catch (Exception ex)
        {
            _suppressed.Add(spec);
            Log.Warn($"Could not open a terminal for '{spec.Script}': {ex.Message}");
            return;
        }

        _surfaces.Add(surface);

        // The launcher completes the task on another thread: only marshal back to the game thread here.
        _ = launch.ContinueWith(
            task => OnLaunchCompleted(surface, task),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnLaunchCompleted(Surface surface, Task<object> task)
    {
        try
        {
            if (_disposed)
            {
                // The game thread no longer drains the queue: close the late surface right here.
                if (task.Status == TaskStatus.RanToCompletion)
                    CloseHandle(task.Result);
                else
                    _ = task.Exception;
                return;
            }

            _queue.PostWork(() => OnLaunched(surface, task));
        }
        catch (Exception ex)
        {
            Log.LogOnce("placer.launchcompleted", LogLevel.Warn, $"Completing a terminal launch failed: {ex.Message}");
        }
    }

    private void OnLaunched(Surface surface, Task<object> task)
    {
        try
        {
            if (task.Status != TaskStatus.RanToCompletion)
            {
                _surfaces.Remove(surface);
                if (!surface.Cancelled)
                {
                    _suppressed.Add(surface.Spec);
                    var reason = task.IsCanceled ? "cancelled" : task.Exception?.GetBaseException().Message ?? "unknown error";
                    Log.Warn($"Could not open a terminal for '{surface.Spec.Script}': {reason}");
                }
                else
                {
                    _ = task.Exception;
                }

                return;
            }

            var handle = task.Result;
            if (_disposed || surface.Cancelled)
            {
                _surfaces.Remove(surface);
                CloseHandle(handle);
                return;
            }

            surface.Handle = handle;
            var stillOpen = LauncherAccess.Watch(handle, () => _queue.PostWork(() => OnSurfaceEnded(surface)));
            if (!stillOpen)
                OnSurfaceEnded(surface);
        }
        catch (Exception ex)
        {
            Log.LogOnce("placer.launched", LogLevel.Warn, $"Tracking a new terminal failed: {ex.Message}");
        }
    }

    private void OnSurfaceEnded(Surface surface)
    {
        try
        {
            // Already removed by a stop/close/dispose we initiated: nothing to remember.
            if (!_surfaces.Remove(surface) || surface.Cancelled)
                return;

            _suppressed.Add(surface.Spec);
        }
        catch (Exception ex)
        {
            Log.LogOnce("placer.ended", LogLevel.Warn, $"Dropping a closed terminal failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- reconcile

    private void OnAvailabilityChanged(bool available)
    {
        // Any thread: hop to the game thread.
        if (_disposed)
            return;

        _queue.PostWork(() =>
        {
            try
            {
                if (_disposed)
                    return;

                _suppressed.Clear();
                if (available)
                    Reconcile(null);
            }
            catch (Exception ex)
            {
                Log.LogOnce("placer.availability", LogLevel.Warn, $"Re-creating terminals after purrTTY became available failed: {ex.Message}");
            }
        });
    }

    private void Reconcile(string? onlyScript)
    {
        if (_disposed)
            return;

        Prune();
        if (_placements.Count == 0 || !IsAvailable)
            return;

        IReadOnlyCollection<string>? vessels = null;
        var vesselsRead = false;
        foreach (var spec in _placements.ToArray())
        {
            if (onlyScript is not null && !string.Equals(spec.Script, onlyScript, StringComparison.Ordinal))
                continue;
            if (!_running.Contains(spec.Script) || _suppressed.Contains(spec) || _surfaces.Any(s => s.Spec == spec))
                continue;

            if (string.Equals(spec.Kind, PlacementKinds.InWorld, StringComparison.Ordinal))
            {
                if (!vesselsRead)
                {
                    vessels = ReadVesselIds();
                    vesselsRead = true;
                }

                if (!VehicleExists(spec.VehicleId, vessels))
                    continue;
            }

            Launch(spec, focus: false, userRequested: false);
        }
    }

    private IReadOnlyCollection<string>? ReadVesselIds()
    {
        try
        {
            return VesselIds?.Invoke();
        }
        catch (Exception ex)
        {
            Log.LogOnce("placer.vessels", LogLevel.Warn, $"Reading the vessel list failed: {ex.Message}");
            return null;
        }
    }

    private static bool VehicleExists(string? vehicleId, IReadOnlyCollection<string>? vessels)
    {
        if (vessels is null)
            return true; // cannot tell: let the launcher decide

        // An empty id means "the controlled vehicle", which needs any vehicle at all.
        return string.IsNullOrEmpty(vehicleId) ? vessels.Count > 0 : vessels.Contains(vehicleId);
    }

    private void Prune()
    {
        foreach (var surface in _surfaces.ToArray())
        {
            if (surface.Handle is null)
                continue;

            try
            {
                if (!LauncherAccess.Describe(surface.Handle).IsOpen)
                    _surfaces.Remove(surface);
            }
            catch
            {
                _surfaces.Remove(surface);
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private Surface? FindSurface(Guid id)
    {
        foreach (var surface in _surfaces)
        {
            if (surface.Handle is null)
                continue;

            try
            {
                if (LauncherAccess.Describe(surface.Handle).Id == id)
                    return surface;
            }
            catch (Exception ex)
            {
                Log.LogOnce("placer.find", LogLevel.Warn, $"Reading a terminal handle failed: {ex.Message}");
            }
        }

        return null;
    }

    private void CloseHandle(object? handle)
    {
        if (handle is null)
            return;

        try
        {
            ObserveFaults(LauncherAccess.Close(handle), "Closing a terminal");
        }
        catch (Exception ex)
        {
            Log.LogOnce("placer.close", LogLevel.Warn, $"Closing a terminal failed: {ex.Message}");
        }
    }

    private static void ObserveFaults(Task task, string what)
    {
        _ = task.ContinueWith(
            t => Log.LogOnce("placer.task:" + what, LogLevel.Warn, $"{what} failed: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SavePlacements()
    {
        try
        {
            _store.Save(_placements);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save the terminal placements: {ex.Message}");
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class Surface(TerminalPlacement spec)
    {
        public TerminalPlacement Spec { get; } = spec;

        /// <summary>The launcher's <c>ITerminalHandle</c> once the launch completed.</summary>
        public object? Handle { get; set; }

        /// <summary>Set when this placer closed (or is about to close) the surface itself.</summary>
        public volatile bool Cancelled;
    }
}
