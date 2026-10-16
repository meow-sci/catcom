using KsaScripting.Core.Catalog;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Terminals;
using purrTTY.Core.Terminal;

namespace KsaScripting.GameMod.Shells;

/// <summary>
///     The purrTTY custom shell (id <c>ksa-script</c>) that is the "mock pty" of a script-owned
///     <see cref="ScriptTerminal"/>: output the script writes reaches the surface through
///     <see cref="ITerminalSink.Write"/>, keys, mouse reports and engine replies reach the script through
///     <see cref="WriteInputAsync"/>. The target script is named by the <c>KSA_SCRIPT</c> environment
///     variable of the launch (factories take no arguments).
/// </summary>
/// <remarks>
///     Threads (docs/research/existing-mods/purrtty-virtual-pty-bridge.md): <c>StartAsync</c> and
///     <c>Dispose</c> run on a pool thread, <c>WriteInputAsync</c> and <c>NotifyTerminalResize</c> on the
///     game / tick thread inside ImGui rendering, <see cref="ITerminalSink"/> calls on the game thread. Every
///     touch of the <see cref="ScriptTerminal"/> is therefore posted to the host's game-thread queue.
///     <c>StopAsync</c> is only called by a session restart; ordinary close calls <see cref="Dispose"/>, so
///     the detach lives in both. The constructor is trivial: the registry probe-instantiates the shell.
/// </remarks>
internal sealed class ScriptTerminalShell : BaseChannelOutputShell, ITerminalSink
{
    /// <summary>The custom shell id the launcher requests.</summary>
    public const string ShellId = "ksa-script";

    /// <summary>The environment variable carrying the script name.</summary>
    public const string ScriptVariable = "KSA_SCRIPT";

    /// <summary>
    ///     <c>0</c> means "attach only": the surface was opened because its script is already running (a persisted
    ///     placement re-created by the placer), so it must never start the script. Absent or anything else means
    ///     "opening a surface for a stopped script starts it".
    /// </summary>
    public const string AutostartVariable = "KSA_SCRIPT_AUTOSTART";

    private const string ErrorOn = "\u001b[31m";
    private const string Reset = "\u001b[0m";

    private string _script = "";
    private ScriptTerminal? _terminal; // game thread only
    private volatile bool _started;
    private volatile bool _disposed;
    private int _terminated;

    /// <inheritdoc />
    public override CustomShellMetadata Metadata { get; } = CustomShellMetadata.Create(
        "ksa-scripting script terminal",
        "The terminal of a ksa-scripting terminal script (set KSA_SCRIPT to the script name)",
        ModInfo.AssemblyVersion,
        "meow sci",
        Array.Empty<string>());

    /// <inheritdoc />
    protected override Task OnStartingAsync(CustomShellStartOptions options, CancellationToken cancellationToken)
    {
        try
        {
            Interlocked.Exchange(ref _terminated, 0);
            _started = true;

            if (!options.EnvironmentVariables.TryGetValue(ScriptVariable, out var script) || string.IsNullOrWhiteSpace(script))
            {
                Fail($"[ksa-scripting] no script given ({ScriptVariable} is not set)", 1);
                return Task.CompletedTask;
            }

            _script = script.Trim();

            // InitialWidth/Height are provisional: purrTTY imposes the real grid on the first rendered frame
            // and reports it through NotifyTerminalResize.
            var cols = Math.Max(1, options.InitialWidth);
            var rows = Math.Max(1, options.InitialHeight);
            var name = _script;
            var autostart = !(options.EnvironmentVariables.TryGetValue(AutostartVariable, out var flag) && flag.Trim() == "0");

            var host = ScriptingHost.Current;
            if (host is null)
            {
                Fail("[ksa-scripting not running]", 1);
                return Task.CompletedTask;
            }

            host.Queue.PostWork(() => AttachOnGameThread(name, cols, rows, autostart));
        }
        catch (Exception ex)
        {
            Log.LogOnce("termshell.start", LogLevel.Warn, $"Starting a script terminal shell failed: {ex.Message}");
            Fail("[ksa-scripting] the script terminal could not start: " + ex.Message, 1);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        // Only a session restart gets here (a restart calls StartAsync again, which re-attaches).
        DetachFromTerminal();
        return Task.CompletedTask;
    }

    /// <summary>Raw input from the surface (keys, SGR mouse reports, engine replies): queued for the script. Never blocks, never throws.</summary>
    /// <param name="data">The bytes, one chunk per call (not one key per call).</param>
    /// <param name="cancellationToken">Ignored.</param>
    /// <returns>A completed task.</returns>
    public override Task WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        try
        {
            if (data.IsEmpty || _disposed)
                return Task.CompletedTask;

            var copy = data.ToArray();
            ScriptingHost.Current?.Queue.PostWork(() =>
            {
                try
                {
                    _terminal?.EnqueueInput(copy);
                }
                catch (Exception ex)
                {
                    Log.LogOnce("termshell.input", LogLevel.Warn, $"Queueing terminal input failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.LogOnce("termshell.write", LogLevel.Warn, $"Handling terminal input failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>The surface's grid changed: forwarded to the terminal on the game thread.</summary>
    /// <param name="width">Columns.</param>
    /// <param name="height">Rows.</param>
    public override void NotifyTerminalResize(int width, int height)
    {
        try
        {
            if (_disposed)
                return;

            ScriptingHost.Current?.Queue.PostWork(() =>
            {
                try
                {
                    _terminal?.NotifyResize(this, width, height);
                }
                catch (Exception ex)
                {
                    Log.LogOnce("termshell.resize", LogLevel.Warn, $"Forwarding a terminal resize failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.LogOnce("termshell.resizepost", LogLevel.Warn, $"Handling a terminal resize failed: {ex.Message}");
        }
    }

    /// <summary>Output of the script (game thread): queued to the surface through the shell's ordered output pump.</summary>
    /// <param name="bytes">Output bytes, escape sequences included.</param>
    void ITerminalSink.Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || _disposed)
            return;

        QueueOutput(bytes.ToArray());
    }

    /// <summary>
    ///     The terminal ended (the script stopped): the final line was already written through
    ///     <see cref="ITerminalSink.Write"/>; now the program "exits" so purrTTY knows. A single-session window shows no
    ///     exit marker, which is why the terminal wrote one.
    /// </summary>
    /// <param name="message">The reason (already displayed).</param>
    void ITerminalSink.Terminate(string message)
    {
        RaiseTerminatedOnce(0, message);
    }

    /// <summary>Teardown lives here: <c>StopAsync</c> is not called when a window closes.</summary>
    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            DetachFromTerminal();
        }

        base.Dispose();
    }

    private void AttachOnGameThread(string script, int cols, int rows, bool autostart)
    {
        try
        {
            if (_disposed)
                return;

            var host = ScriptingHost.Current;
            if (host is null)
            {
                Fail("[ksa-scripting not running]", 1);
                return;
            }

            var terminal = host.Terminals.ForScript(script);
            if (terminal is null)
            {
                var info = host.Catalog.Get(script);
                if (info is null)
                {
                    Fail($"[ksa-scripting] unknown script '{script}'", 1);
                    return;
                }

                // A running service without terminal=true gets its terminal now; a stopped script is started
                // (unless this surface only attaches), and its terminal=true header creates the terminal before
                // main runs.
                if (info.State != ScriptState.Service)
                {
                    if (!autostart)
                    {
                        Fail($"[ksa-scripting] script '{script}' is not running", 1);
                        return;
                    }

                    host.Catalog.Start(script);
                    terminal = host.Terminals.ForScript(script);

                    var started = host.Catalog.Get(script);
                    if (terminal is null && started is { State: ScriptState.Error or ScriptState.Disabled })
                    {
                        Fail($"[ksa-scripting] script '{script}' did not start: {started.LastError ?? started.State.ToString()}", 1);
                        return;
                    }
                }
            }

            terminal ??= host.Terminals.Create(script);
            _terminal = terminal;
            terminal.Attach(this, cols, rows);
        }
        catch (Exception ex)
        {
            Log.LogOnce("termshell.attach", LogLevel.Warn, $"Attaching a terminal surface to '{script}' failed: {ex}");
            Fail("[ksa-scripting] could not attach to the script terminal: " + ex.Message, 1);
        }
    }

    private void DetachFromTerminal()
    {
        try
        {
            if (!_started)
                return; // never started: the registry probe instance

            var host = ScriptingHost.Current;
            host?.Queue.PostWork(() =>
            {
                try
                {
                    var terminal = _terminal;
                    _terminal = null;
                    terminal?.Detach(this);
                }
                catch (Exception ex)
                {
                    Log.LogOnce("termshell.detach", LogLevel.Warn, $"Detaching a terminal surface failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.LogOnce("termshell.detachpost", LogLevel.Warn, $"Detaching a terminal surface failed: {ex.Message}");
        }
    }

    private void Fail(string message, int exitCode)
    {
        try
        {
            QueueOutput($"{ErrorOn}{message}{Reset}\r\n");
        }
        catch
        {
            // The output channel is gone; the exit below is still reported.
        }

        RaiseTerminatedOnce(exitCode, message);
    }

    private void RaiseTerminatedOnce(int exitCode, string reason)
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0)
            return;

        try
        {
            RaiseTerminated(exitCode, reason);
        }
        catch (Exception ex)
        {
            Log.LogOnce("termshell.terminated", LogLevel.Warn, $"Raising the terminal exit failed: {ex.Message}");
        }
    }
}
