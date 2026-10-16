using System.Runtime.CompilerServices;
using System.Text;
using KsaScripting.Core.Config;
using KsaScripting.Core.Editor;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Scheduling;
using purrTTY.Core.Terminal;

namespace KsaScripting.GameMod.Editor;

/// <summary>One open editor session (Overview tab / Runtimes tab).</summary>
public sealed record EditorSessionInfo(string ScriptName, string FullPath, int Port, bool Connected);

/// <summary>
///     Launches the shipped Neovim on a script file inside a purrTTY window through the purrTTY
///     launcher contract, connects the msgpack-RPC client over loopback TCP, forwards
///     <c>ksa_saved</c> notifications and pushes load errors back as diagnostics
///     (docs/research/editor/neovim-tcp-launch.md, save-detection-and-reload.md). Game thread only:
///     every background continuation (launch completion, RPC connect, lifecycle events) marshals its
///     state changes back through <see cref="GameThreadQueue.PostWork"/>, and RPC writes run off the
///     game thread. Nothing here throws to the caller.
/// </summary>
public sealed class EditorLauncher : IDisposable
{
    private const long AvailabilityCacheMs = 1000;
    private const long LaunchTimeoutMs = 20_000;
    private const int MaxEchoLength = 120;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ModPaths _paths;
    private readonly ScriptingConfig _config;
    private readonly GameThreadQueue _queue;
    private readonly Action<string> _onSaved;
    private readonly List<Session> _sessions = [];
    private bool _disposed;
    private bool _nvimChecked;
    private bool _nvimExists;
    private long _nvimCheckedAt;

    /// <summary>Creates the launcher; <paramref name="onSaved"/> receives full paths saved in nvim (called on any thread).</summary>
    public EditorLauncher(ModPaths paths, ScriptingConfig config, GameThreadQueue queue, Action<string> onSaved)
    {
        _paths = paths;
        _config = config;
        _queue = queue;
        _onSaved = onSaved;
    }

    /// <summary>True when editing can work now (editor enabled, nvim present, purrTTY launcher available).</summary>
    public bool IsAvailable => _config.EditorEnabled && NvimExists() && LauncherAvailable();

    /// <summary>Why <see cref="IsAvailable"/> is false.</summary>
    public string? UnavailableReason
    {
        get
        {
            if (!_config.EditorEnabled)
                return "editor disabled in ksa-scripting.toml";
            if (!NvimExists())
                return $"Neovim not found at {_paths.NvimExecutable} (run tools/fetch-nvim.sh)";
            if (!LauncherAvailable())
                return "purrTTY launcher unavailable (install purrTTY with the launcher contract)";
            return null;
        }
    }

    /// <summary>Open sessions.</summary>
    public IReadOnlyList<EditorSessionInfo> Sessions
    {
        get
        {
            if (_sessions.Count == 0)
                return [];

            var infos = new EditorSessionInfo[_sessions.Count];
            for (var i = 0; i < infos.Length; i++)
            {
                var s = _sessions[i];
                infos[i] = new EditorSessionInfo(s.ScriptName, s.FullPath, s.Port, s.Connected);
            }

            return infos;
        }
    }

    /// <summary>Opens (or focuses the existing session of) the file, optionally at a line. Never throws.</summary>
    public void Open(string scriptName, string fullPath, int? line = null)
    {
        try
        {
            OpenCore(scriptName, Path.GetFullPath(fullPath), line);
        }
        catch (Exception ex)
        {
            Log.LogOnce("editor.open." + ex.GetType().Name, LogLevel.Warn, $"could not open the editor for '{scriptName}': {ex.Message}");
        }
    }

    /// <summary>Shows a load error as an nvim diagnostic in the file's open session (no-op without one).</summary>
    public void ShowLoadError(string fullPath, int line, int column, string message)
    {
        try
        {
            if (FindConnected(fullPath) is not { } session)
                return;

            var client = session.Client!;
            var path = session.FullPath;
            Observe(
                Task.Run(async () =>
                {
                    await client.SetDiagnosticAsync(path, line, column, message).ConfigureAwait(false);
                    await client.EchoAsync("ksa-scripting: " + Summarize(message), error: true).ConfigureAwait(false);
                }),
                "diagnostic");
        }
        catch (Exception ex)
        {
            Log.LogOnce("editor.showerror." + ex.GetType().Name, LogLevel.Warn, "could not show a load error in the editor: " + ex.Message);
        }
    }

    /// <summary>Clears ksa-scripting diagnostics for the file.</summary>
    public void ClearDiagnostics(string fullPath)
    {
        try
        {
            if (FindConnected(fullPath) is not { } session)
                return;

            var client = session.Client!;
            var path = session.FullPath;
            Observe(Task.Run(() => client.ClearDiagnosticsAsync(path)), "clear-diagnostics");
        }
        catch (Exception ex)
        {
            Log.LogOnce("editor.cleardiag." + ex.GetType().Name, LogLevel.Warn, "could not clear editor diagnostics: " + ex.Message);
        }
    }

    /// <summary>Per-frame housekeeping (called from <c>[StarMapAfterGui]</c>).</summary>
    public void Pump()
    {
        if (_sessions.Count == 0)
            return;

        try
        {
            var now = Environment.TickCount64;
            for (var i = _sessions.Count - 1; i >= 0; i--)
            {
                var session = _sessions[i];
                if (session.Handle is { } handle)
                {
                    if (!handle.IsOpen)
                        DropSession(session, closeTerminal: false);
                }
                else if (now - session.StartedAt > LaunchTimeoutMs)
                {
                    Log.LogOnce("editor.launch-timeout", LogLevel.Warn, $"purrTTY did not open the editor window for '{session.ScriptName}' in time; giving up.");
                    DropSession(session, closeTerminal: true);
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("editor.pump." + ex.GetType().Name, LogLevel.Warn, "editor housekeeping failed: " + ex.Message);
        }
    }

    /// <summary>Closes every session.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            foreach (var session in _sessions.ToArray())
                DropSession(session, closeTerminal: true);
            _sessions.Clear();
        }
        catch (Exception ex)
        {
            Log.LogOnce("editor.dispose." + ex.GetType().Name, LogLevel.Warn, "could not close every editor session: " + ex.Message);
        }
    }

    // The registry read lives in its own non-inlined method so a missing contract assembly fails
    // here (inside a try/catch) instead of poisoning the JIT of every caller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ITerminalLauncher? CurrentLauncher() => TerminalLauncherRegistry.Current;

    private static bool LauncherAvailable()
    {
        try
        {
            return CurrentLauncher() is { IsAvailable: true };
        }
        catch (Exception ex)
        {
            Log.LogOnce("editor.launcher." + ex.GetType().Name, LogLevel.Warn, "purrTTY launcher contract could not be read: " + ex.Message);
            return false;
        }
    }

    // The path with every symlinked directory component resolved; the input when anything fails.
    private static string RealPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? "";
            var current = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = target.FullName;
            }

            return current;
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static string Summarize(string message)
    {
        var firstLine = message.Split('\n', 2)[0].Trim();
        return firstLine.Length <= MaxEchoLength ? firstLine : firstLine[..MaxEchoLength] + "…";
    }

    private static void Observe(Task task, string what)
    {
        task.ContinueWith(
            t =>
            {
                var ex = t.Exception?.GetBaseException();
                Log.LogOnce($"editor.{what}.{ex?.GetType().Name}", LogLevel.Warn, $"editor {what} failed: {ex?.Message}");
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string Quote(string value) => value.Contains(' ') || value.Contains('"') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private bool NvimExists()
    {
        var now = Environment.TickCount64;
        if (!_nvimChecked || now - _nvimCheckedAt >= AvailabilityCacheMs)
        {
            _nvimChecked = true;
            _nvimCheckedAt = now;
            _nvimExists = File.Exists(_paths.NvimExecutable);
        }

        return _nvimExists;
    }

    private Session? Find(string fullPath)
    {
        foreach (var session in _sessions)
        {
            if (PathComparer.Equals(session.FullPath, fullPath))
                return session;
        }

        return null;
    }

    private Session? FindConnected(string fullPath)
    {
        if (_sessions.Count == 0)
            return null;

        var session = Find(Path.GetFullPath(fullPath));
        return session is { Client.IsConnected: true } ? session : null;
    }

    private void OpenCore(string scriptName, string fullPath, int? line)
    {
        if (_disposed)
            return;

        if (Find(fullPath) is { } existing)
        {
            if (existing.Handle is null)
            {
                // Still launching: remember the requested line for when the RPC connection is up.
                existing.PendingLine = line ?? existing.PendingLine;
                return;
            }

            if (existing.Handle.IsOpen)
            {
                Observe(existing.Handle.FocusAsync(), "focus");
                if (line is { } target && existing.Client is { IsConnected: true } client)
                    Observe(Task.Run(() => client.GoToLineAsync(fullPath, target)), "goto-line");
                return;
            }

            DropSession(existing, closeTerminal: false);
        }

        var port = LoopbackPorts.PickFree(_config.EditorPortMin, _config.EditorPortMax);
        if (!_config.EditorEnabled || !NvimExists() || CurrentLauncher() is not { IsAvailable: true } launcher)
        {
            LogManualRecipe(scriptName, fullPath, line, port);
            return;
        }

        var session = new Session(scriptName, fullPath, port, Environment.TickCount64);
        _sessions.Add(session);
        Task<ITerminalHandle> launch;
        try
        {
            launch = launcher.LaunchAsync(BuildRequest(scriptName, fullPath, line, port), session.Cts.Token);
        }
        catch
        {
            _sessions.Remove(session);
            throw;
        }

        _ = AwaitLaunchAsync(session, launch);
    }

    private TerminalLaunchRequest BuildRequest(string scriptName, string fullPath, int? line, int port)
    {
        var program = new TerminalProgramSpec
        {
            Program = _paths.NvimExecutable,
            WorkingDirectory = _paths.ScriptsDir,
        };
        program.Arguments.AddRange(NvimArguments(fullPath, line, port));
        foreach (var (key, value) in NvimEnvironment())
            program.Environment[key] = value;

        return new TerminalLaunchRequest
        {
            Placement = TerminalPlacementKind.Window,
            Program = program,
            Window = new TerminalWindowOptions
            {
                Name = "ksa-edit:" + scriptName,
                Title = "nvim — " + scriptName,
                ThemeName = string.IsNullOrEmpty(_config.EditorTheme) ? null : _config.EditorTheme,
                Cols = 120,
                Rows = 36,
                Focus = true,
            },
        };
    }

    private List<string> NvimArguments(string fullPath, int? line, int port)
    {
        var args = new List<string> { "--clean", "-u", _paths.EditorInitLua, "--listen", $"127.0.0.1:{port}" };
        if (line is > 0)
            args.Add($"+{line}");
        args.Add(fullPath);
        return args;
    }

    private List<KeyValuePair<string, string>> NvimEnvironment()
    {
        var env = new List<KeyValuePair<string, string>>
        {
            new("NVIM_APPNAME", "ksa-editor"),
            new("XDG_CONFIG_HOME", _paths.EditorConfigHome),
            new("XDG_DATA_HOME", _paths.EditorDataHome),
            new("XDG_STATE_HOME", _paths.EditorStateHome),
            new("XDG_CACHE_HOME", _paths.EditorCacheHome),
            new("KSA_LSP_DIR", _paths.LspDir),
            new("KSA_NOSHELL", _paths.NoShell),
            new("KSA_SCRIPTS_DIR", _paths.ScriptsDir),
            new("COLORTERM", "truecolor"),
        };
        if (!OperatingSystem.IsWindows())
            env.Add(new("TERM", "xterm-256color"));
        return env;
    }

    private void LogManualRecipe(string scriptName, string fullPath, int? line, int port)
    {
        var program = NvimExists() ? _paths.NvimExecutable : "nvim";
        var text = new StringBuilder();
        text.Append($"cannot open '{scriptName}' in the in-game editor ({UnavailableReason ?? "purrTTY launcher unavailable"}). To edit it yourself, run in a terminal:");
        text.Append($"\n  cd {Quote(_paths.ScriptsDir)}");
        foreach (var (key, value) in NvimEnvironment())
            text.Append(OperatingSystem.IsWindows() ? $"\n  set \"{key}={value}\"" : $"\n  export {key}={Quote(value)}");
        text.Append($"\n  {Quote(program)} {string.Join(' ', NvimArguments(fullPath, line, port).Select(Quote))}");
        Log.Info(text.ToString());
    }

    private async Task AwaitLaunchAsync(Session session, Task<ITerminalHandle> launch)
    {
        try
        {
            var handle = await launch.ConfigureAwait(false);
            _queue.PostWork(() => OnLaunched(session, handle));
        }
        catch (OperationCanceledException)
        {
            _queue.PostWork(() => DropSession(session, closeTerminal: false));
        }
        catch (Exception ex)
        {
            _queue.PostWork(() =>
            {
                Log.LogOnce("editor.launch." + ex.GetType().Name, LogLevel.Warn, $"purrTTY could not open the editor window for '{session.ScriptName}': {ex.Message}");
                DropSession(session, closeTerminal: false);
            });
        }
    }

    // Game thread (posted).
    private void OnLaunched(Session session, ITerminalHandle handle)
    {
        try
        {
            if (_disposed || session.Dropped)
            {
                Observe(handle.CloseAsync(), "close");
                return;
            }

            session.Handle = handle;
            handle.Lifecycle += OnLifecycle;
            if (!handle.IsOpen || handle.ExitCode is not null)
            {
                // Closed or exited before we subscribed (nvim failed to start): nothing to connect to.
                DropSession(session, closeTerminal: false);
                return;
            }

            _ = Task.Run(() => ConnectAsync(session));
        }
        catch (Exception ex)
        {
            Log.LogOnce("editor.launched." + ex.GetType().Name, LogLevel.Warn, "could not finish opening the editor: " + ex.Message);
        }
    }

    // Raised on the game's main thread by purrTTY; state changes are posted regardless.
    private void OnLifecycle(ITerminalHandle handle, TerminalLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent is TerminalLifecycleEvent.Exited or TerminalLifecycleEvent.Closed)
            _queue.PostWork(() => OnHandleEnded(handle));
    }

    // Game thread (posted).
    private void OnHandleEnded(ITerminalHandle handle)
    {
        try
        {
            foreach (var session in _sessions.ToArray())
            {
                if (ReferenceEquals(session.Handle, handle))
                    DropSession(session, closeTerminal: false);
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("editor.ended." + ex.GetType().Name, LogLevel.Warn, "could not release an editor session: " + ex.Message);
        }
    }

    // Background thread.
    private async Task ConnectAsync(Session session)
    {
        NvimRpcClient? client = null;
        try
        {
            var token = session.Cts.Token;
            client = await NvimRpcClient.ConnectAsync("127.0.0.1", session.Port, ConnectTimeout, token).ConfigureAwait(false);
            var connecting = client;
            var realPath = RealPath(session.FullPath);
            connecting.Notification += (method, args) => OnNotification(session, realPath, method, args);
            connecting.Disconnected += _ => _queue.PostWork(() => OnDisconnected(session, connecting));
            await connecting.InitializeAsync(ct: token).ConfigureAwait(false);
            _queue.PostWork(() => OnConnected(session, connecting));
            client = null; // ownership moved to the posted work
        }
        catch (OperationCanceledException)
        {
            // The session was dropped while connecting.
        }
        catch (Exception ex)
        {
            Log.LogOnce(
                "editor.connect." + ex.GetType().Name,
                LogLevel.Warn,
                $"could not connect to Neovim's RPC port 127.0.0.1:{session.Port} ({ex.Message}); saves in the editor will not trigger a reload.");
        }
        finally
        {
            client?.Dispose();
        }
    }

    // Reader thread. Neovim reports the path with symlinked directories resolved (macOS /var -> /private/var),
    // so a save of the session's own file is reported under the path the host asked for.
    private void OnNotification(Session session, string realPath, string method, IReadOnlyList<object?> args)
    {
        if (method != "ksa_saved" || args.Count == 0 || args[0] is not string path)
            return;

        try
        {
            _onSaved(PathComparer.Equals(path, session.FullPath) || PathComparer.Equals(path, realPath) ? session.FullPath : path);
        }
        catch (Exception ex)
        {
            Log.LogOnce("editor.onsaved." + ex.GetType().Name, LogLevel.Warn, "handling a save notification failed: " + ex.Message);
        }
    }

    // Game thread (posted).
    private void OnConnected(Session session, NvimRpcClient client)
    {
        try
        {
            if (_disposed || session.Dropped || !client.IsConnected)
            {
                client.Dispose();
                return;
            }

            session.Client = client;
            session.Connected = true;
            if (session.PendingLine is { } line)
            {
                session.PendingLine = null;
                var path = session.FullPath;
                Observe(Task.Run(() => client.GoToLineAsync(path, line)), "goto-line");
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("editor.connected." + ex.GetType().Name, LogLevel.Warn, "could not finish connecting the editor: " + ex.Message);
        }
    }

    // Game thread (posted).
    private void OnDisconnected(Session session, NvimRpcClient client)
    {
        try
        {
            if (ReferenceEquals(session.Client, client))
            {
                session.Client = null;
                session.Connected = false;
            }

            client.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogOnce("editor.disconnected." + ex.GetType().Name, LogLevel.Warn, "could not release an editor connection: " + ex.Message);
        }
    }

    private void DropSession(Session session, bool closeTerminal)
    {
        if (session.Dropped)
            return;

        session.Dropped = true;
        _sessions.Remove(session);
        session.Cts.Cancel();
        session.Connected = false;
        session.Client?.Dispose();
        session.Client = null;
        if (session.Handle is { } handle)
        {
            handle.Lifecycle -= OnLifecycle;
            if (closeTerminal)
                Observe(handle.CloseAsync(), "close");
        }
    }

    private sealed class Session(string scriptName, string fullPath, int port, long startedAt)
    {
        public string ScriptName { get; } = scriptName;

        public string FullPath { get; } = fullPath;

        public int Port { get; } = port;

        public long StartedAt { get; } = startedAt;

        public CancellationTokenSource Cts { get; } = new();

        public ITerminalHandle? Handle { get; set; }

        public NvimRpcClient? Client { get; set; }

        public bool Connected { get; set; }

        public bool Dropped { get; set; }

        public int? PendingLine { get; set; }
    }
}
