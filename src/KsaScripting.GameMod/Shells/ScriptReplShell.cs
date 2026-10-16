using KsaScripting.Core.Logging;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;
using purrTTY.Core.Terminal;

namespace KsaScripting.GameMod.Shells;

/// <summary>
///     A line-buffered REPL (<c>lua&gt;</c> / <c>ts&gt;</c>) for one script runtime, registered as a purrTTY custom
///     shell. <see cref="ExecuteCommandLine"/> runs on the game thread inside ImGui rendering while the vehicle
///     solver may be running, so it never evaluates inline: the line is posted to the host and evaluated in the
///     next Step slot, where the result (or the error) is printed and the prompt returns.
/// </summary>
/// <remarks>
///     Each line is its own tiny script (<c>repl/&lt;runtime&gt;-&lt;n&gt;</c>) exporting <c>__repl(ctx)</c>: values do
///     not carry over between lines (use <c>ksa.state</c>). A module that registered hooks or has waits pending
///     stays loaded until <c>:reset</c> or until the shell is disposed; any other module is released with the next
///     evaluation (after its continuations ran). Output logged by a REPL script, also asynchronously, is
///     mirrored into the terminal through <c>HostFacade.Output</c>. Lines starting with <c>:</c> are REPL commands.
/// </remarks>
internal sealed class ScriptReplShell : BaseLineBufferedShell
{
    private const string Red = "\u001b[31m";
    private const string Yellow = "\u001b[33m";
    private const string Dim = "\u001b[2m";
    private const string Reset = "\u001b[0m";

    private static long s_counter;

    private readonly ReplFlavor _flavor;
    private readonly List<LoadedLine> _modules = []; // game thread only
    private readonly HashSet<string> _owned = new(StringComparer.Ordinal); // game thread only
    private Action<string, LogEntry>? _outputHandler; // game thread only
    private volatile ScriptingHost? _host; // written on the game thread
    private volatile bool _disposed;

    /// <summary>Creates the shell (trivial: the registry probe-instantiates it).</summary>
    /// <param name="flavor">Which REPL this is.</param>
    public ScriptReplShell(ReplFlavor flavor)
    {
        _flavor = flavor ?? throw new ArgumentNullException(nameof(flavor));
        Metadata = CustomShellMetadata.Create(
            flavor.DisplayName,
            $"Evaluate {(flavor.RuntimeId == "lua" ? "Lua" : "TypeScript")} one line at a time in the running game (type :help)",
            ModInfo.AssemblyVersion,
            "meow sci",
            "repl");
    }

    /// <inheritdoc />
    public override CustomShellMetadata Metadata { get; }

    /// <inheritdoc />
    protected override Task OnStartingAsync(CustomShellStartOptions options, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    protected override Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        // Only a session restart gets here; ordinary close goes through Dispose.
        PostCleanup();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void SendInitialOutput()
    {
        try
        {
            var host = ScriptingHost.Current;
            var status = host?.RuntimeStatuses.FirstOrDefault(s => s.Id == _flavor.RuntimeId);
            var banner = $"{_flavor.DisplayName}  -  :help for commands\r\n";
            if (host is null)
                banner += $"{Red}ksa-scripting is not running.{Reset}\r\n";
            else if (status is { Available: false })
                banner += $"{Red}This runtime is not available: {status.Reason}{Reset}\r\n";

            QueueOutput(banner);
            QueueOutput(_flavor.Prompt);
        }
        catch (Exception ex)
        {
            Log.LogOnce("repl.banner", LogLevel.Warn, $"The REPL banner failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    protected override string GetPrompt() => _flavor.Prompt;

    /// <inheritdoc />
    protected override void HandleClearScreen() => SendOutput("\u001b[2J\u001b[H");

    /// <inheritdoc />
    protected override void ExecuteCommandLine(string commandLine)
    {
        try
        {
            var line = commandLine.Trim();
            if (line.Length == 0)
            {
                SendPrompt();
                return;
            }

            if (line[0] == ':')
            {
                HandleReplCommand(line);
                return;
            }

            var host = ScriptingHost.Current;
            if (host is null)
            {
                PrintLine(Red + "ksa-scripting is not running." + Reset);
                SendPrompt();
                return;
            }

            // Never evaluate inline: the solver may be running. The Step slot prints the result and the prompt.
            host.Post(() => EvaluateOnGameThread(host, line));
        }
        catch (Exception ex)
        {
            Log.LogOnce("repl.execute", LogLevel.Warn, $"A REPL line failed: {ex.Message}");
            PrintLine(Red + "error: " + ex.Message + Reset);
            SendPrompt();
        }
    }

    /// <summary>Teardown lives here: <c>StopAsync</c> is not called when a window closes.</summary>
    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            PostCleanup();
        }

        base.Dispose();
    }

    private void HandleReplCommand(string line)
    {
        switch (line.ToLowerInvariant())
        {
            case ":help":
                PrintLine(
                    "Each line is evaluated on its own in the next game step; use ksa.state to keep values.\n"
                    + "  <expression>   evaluate and print the result\n"
                    + "  <statement>    run it (hooks it registers stay alive until :reset)\n"
                    + "  :clear         clear the screen\n"
                    + "  :reset         unload everything this REPL registered\n"
                    + "  :help          this text");
                SendPrompt();
                break;
            case ":clear":
                HandleClearScreen();
                SendPrompt();
                break;
            case ":reset":
                {
                    var host = ScriptingHost.Current;
                    if (host is null)
                    {
                        PrintLine(Red + "ksa-scripting is not running." + Reset);
                        SendPrompt();
                        break;
                    }

                    host.Post(() =>
                    {
                        try
                        {
                            UnloadAll();
                            PrintLine(Dim + "reset." + Reset);
                        }
                        catch (Exception ex)
                        {
                            PrintLine(Red + "error: " + ex.Message + Reset);
                        }
                        finally
                        {
                            SendPrompt();
                        }
                    });
                    break;
                }

            default:
                PrintLine(Yellow + $"unknown REPL command '{line}' (try :help)" + Reset);
                SendPrompt();
                break;
        }
    }

    // ---------------------------------------------------------------- evaluation (game thread)

    private void EvaluateOnGameThread(ScriptingHost host, string line)
    {
        try
        {
            if (_disposed)
                return;

            AdoptHost(host);

            var runtime = host.Runtimes.ForId(_flavor.RuntimeId);
            if (runtime is null || !runtime.IsAvailable)
            {
                PrintLine(Red + $"{_flavor.DisplayName} is not available: {runtime?.UnavailableReason ?? "the runtime is not loaded"}" + Reset);
                return;
            }

            SweepIdleModules(host);

            var number = Interlocked.Increment(ref s_counter);
            var name = $"repl/{_flavor.RuntimeId}-{number}";
            var path = Path.Combine(host.Paths.ScriptsDir, "repl", $"{_flavor.RuntimeId}-{number}{_flavor.Extension}");
            host.Scheduler.SetScriptPath(name, path);
            _owned.Add(name);

            var module = LoadLine(host, runtime, name, path, line, out var loadError);
            if (module is null)
            {
                _owned.Remove(name);
                host.Hooks.RemoveAll(name);
                host.Scheduler.ForgetScript(name);
                PrintLine(Red + "error: " + (loadError ?? "the line could not be loaded") + Reset);
                return;
            }

            var loaded = new LoadedLine(name, runtime, module);
            _modules.Add(loaded);

            ScriptValue result = ScriptValue.Null;
            ScriptCallable? fn = null;
            try
            {
                fn = runtime.GetExport(module, "__repl");
                result = host.Scheduler.InvokeGuarded(name, fn, [host.Scheduler.BuildContext("main", name)]);
            }
            catch (Exception ex)
            {
                PrintLine(Red + "error: " + ex.Message + Reset);
            }
            finally
            {
                try
                {
                    fn?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.LogOnce("repl.dispose", LogLevel.Warn, $"Releasing a REPL function failed: {ex.Message}");
                }
            }

            if (!result.IsNull)
                PrintLine(result.ToJson());
        }
        catch (Exception ex)
        {
            Log.LogOnce("repl.evaluate", LogLevel.Warn, $"A REPL evaluation failed: {ex}");
            PrintLine(Red + "error: " + ex.Message + Reset);
        }
        finally
        {
            SendPrompt();
        }
    }

    private ScriptModule? LoadLine(ScriptingHost host, IScriptRuntime runtime, string name, string path, string line, out string? error)
    {
        error = null;
        foreach (var source in _flavor.Candidates(line))
        {
            try
            {
                using (host.Facade.EnterScript(name))
                {
                    return runtime.Load(new ScriptSource(name, path, source, runtime.Language));
                }
            }
            catch (ScriptLoadException ex)
            {
                error = ex.Message;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        return null;
    }

    private void AdoptHost(ScriptingHost host)
    {
        if (ReferenceEquals(_host, host))
            return;

        // A different (restarted) host: the old one's modules died with its runtimes.
        _modules.Clear();
        _owned.Clear();
        _host = host;

        _outputHandler = OnFacadeOutput;
        host.Facade.Output += _outputHandler;
    }

    private void OnFacadeOutput(string script, LogEntry entry)
    {
        try
        {
            if (_disposed || !_owned.Contains(script))
                return;

            var text = entry.Message;
            var prefix = $"[{script}] ";
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                text = text[prefix.Length..];

            PrintLine(entry.Level switch
            {
                LogLevel.Error => Red + text + Reset,
                LogLevel.Warn => Yellow + text + Reset,
                LogLevel.Debug => Dim + text + Reset,
                _ => text,
            });
        }
        catch (Exception ex)
        {
            Log.LogOnce("repl.output", LogLevel.Warn, $"Mirroring REPL output failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- unloading (game thread)

    private void SweepIdleModules(ScriptingHost host)
    {
        foreach (var loaded in _modules.ToArray())
        {
            if (HasLiveWork(host, loaded.Name))
                continue;

            _modules.Remove(loaded);
            Unload(host, loaded);
        }
    }

    private void UnloadAll()
    {
        var host = _host;
        if (host is null)
            return;

        foreach (var loaded in _modules.ToArray())
        {
            _modules.Remove(loaded);
            Unload(host, loaded);
        }
    }

    private static bool HasLiveWork(ScriptingHost host, string name)
        => host.Hooks.ForScript(name).Count > 0 || host.Scheduler.PendingWaitIds(name).Count > 0;

    private void Unload(ScriptingHost host, LoadedLine loaded)
    {
        _owned.Remove(loaded.Name);
        try
        {
            host.Scheduler.CancelWaits(loaded.Name);
            host.Hooks.RemoveAll(loaded.Name);
            loaded.Runtime.Unload(loaded.Module);
            host.Scheduler.ForgetScript(loaded.Name);
        }
        catch (Exception ex)
        {
            Log.LogOnce("repl.unload", LogLevel.Warn, $"Unloading a REPL line failed: {ex.Message}");
        }
    }

    private void PostCleanup()
    {
        try
        {
            var host = _host;
            if (host is null)
                return; // nothing was ever evaluated

            host.Post(() =>
            {
                try
                {
                    UnloadAll();
                    if (_outputHandler is not null)
                    {
                        host.Facade.Output -= _outputHandler;
                        _outputHandler = null;
                    }

                    _host = null;
                }
                catch (Exception ex)
                {
                    Log.LogOnce("repl.cleanup", LogLevel.Warn, $"Cleaning up a REPL failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.LogOnce("repl.cleanuppost", LogLevel.Warn, $"Cleaning up a REPL failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- output

    private void PrintLine(string text) => SendOutput(text.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n");

    private sealed record LoadedLine(string Name, IScriptRuntime Runtime, ScriptModule Module);
}
