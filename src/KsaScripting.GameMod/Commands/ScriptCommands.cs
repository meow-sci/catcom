using System.Runtime.CompilerServices;
using System.Text;
using Brutal.ImGuiApi.Abstractions;
using KSA;
using KsaScripting.Core.Api;
using KsaScripting.Core.Catalog;
using KsaScripting.Core.Hooks;
using KsaScripting.Core.Logging;

namespace KsaScripting.GameMod.Commands;

/// <summary>
///     The <c>script</c> console verb (docs/research/ksa-host/console-and-commands.md). Registered with the game's
///     <c>TerminalInterface</c> under a private owner token and removed with <c>UnregisterObject</c>. Console
///     commands run on the main thread while the vehicle solver may be running, so everything that touches scripts
///     is posted to the next Step slot; results are printed through <see cref="Log"/> (the log sink mirrors them
///     into the console). Nothing here throws.
/// </summary>
internal static class ScriptCommands
{
    private const string Usage =
        "script run <name>        load a script and call main(ctx) once\n"
        + "script start <name>      start a script as a service (runs main(ctx), keeps its hooks)\n"
        + "script stop <name>       stop a service and unload the script\n"
        + "script reload <name>     reload a script from disk (a running service restarts)\n"
        + "script list              list scripts with language, state and last error\n"
        + "script edit <name>       open the script in the shipped Neovim editor\n"
        + "script stubs             (re)write the editor type stubs into the scripts folder\n"
        + "script events            list the script events with their counters\n"
        + "script call <cmd> [args] invoke a script command registered with ksa.command\n"
        + "<name> is a script name, with or without extension / folder, when unambiguous.";

    private static readonly object OwnerToken = new();
    private static ScriptingHost? s_host;

    /// <summary>Registers the <c>script</c> command. Never throws.</summary>
    /// <param name="host">The running host the command operates on.</param>
    public static void Register(ScriptingHost host)
    {
        s_host = host;
        try
        {
            RegisterWithConsole();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not register the 'script' console command: {ex.Message}");
        }
    }

    /// <summary>Removes the command registered by <see cref="Register"/>. Never throws.</summary>
    public static void Unregister()
    {
        s_host = null;
        try
        {
            UnregisterFromConsole();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not unregister the 'script' console command: {ex.Message}");
        }
    }

    // The game types live in their own NoInlining methods so a failure to load them is an exception at the
    // call site above instead of a JIT failure of Register / Unregister themselves.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterWithConsole()
    {
        if (Program.ConsoleWindow is null)
        {
            Log.Warn("The game console is not ready; the 'script' command was not registered.");
            return;
        }

        Program.TerminalInterface.RegisterCommand(new Action<string>(Script), OwnerToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UnregisterFromConsole()
    {
        if (Program.ConsoleWindow is not null)
            Program.TerminalInterface.UnregisterObject(OwnerToken);
    }

    [TerminalAction("script", "ksa-scripting: script run|start|stop|reload|list|edit|stubs|events|call <command> [args]", ArgParseMode.Unparsed)]
    private static void Script(string args)
    {
        try
        {
            var host = s_host;
            if (host is null)
            {
                Log.Warn("ksa-scripting is not running.");
                return;
            }

            SplitFirst((args ?? "").Trim(), out var verb, out var rest);
            switch (verb.ToLowerInvariant())
            {
                case "":
                case "help":
                case "?":
                    Log.Info("Usage:\n" + Usage);
                    break;
                case "run":
                    OnScript(host, "run", rest, (h, name) => h.Catalog.Run(name));
                    break;
                case "start":
                    OnScript(host, "start", rest, (h, name) => h.Catalog.Start(name));
                    break;
                case "stop":
                    OnScript(host, "stop", rest, (h, name) => h.Catalog.Stop(name));
                    break;
                case "reload":
                    OnScript(host, "reload", rest, (h, name) => h.Catalog.Reload(name));
                    break;
                case "edit":
                    OnScript(host, "edit", rest, Edit, report: false);
                    break;
                case "list":
                    host.Post(() => Guarded("list", () => List(host)));
                    break;
                case "stubs":
                    host.Post(() => Guarded("stubs", () => Stubs(host)));
                    break;
                case "events":
                    host.Post(() => Guarded("events", () => Events(host)));
                    break;
                case "call":
                    Call(host, rest);
                    break;
                default:
                    Log.Warn($"Unknown script subcommand '{verb}'.\n{Usage}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("command.script", LogLevel.Warn, $"The 'script' command failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- subcommands

    private static void OnScript(ScriptingHost host, string verb, string argument, Action<ScriptingHost, string> act, bool report = true)
    {
        if (ScriptNameResolver.Normalize(argument).Length == 0)
        {
            Log.Info($"usage: script {verb} <name>");
            return;
        }

        host.Post(() => Guarded(verb, () =>
        {
            // Pick up files created since the last watcher event, then resolve against the catalog.
            host.Catalog.Scan();
            var match = ScriptNameResolver.Resolve(argument, host.Catalog.All(), out var normalized, out var problem);
            if (problem is not null)
            {
                Log.Warn(problem);
                return;
            }

            if (match is null)
            {
                Log.Warn($"script '{normalized}' not found (try 'script list').");
                return;
            }

            act(host, match.Name);
            if (report)
                ReportState(host, verb, match.Name);
        }));
    }

    private static void Edit(ScriptingHost host, string name)
    {
        var info = host.Catalog.Get(name);
        if (info is null)
            return;

        if (!host.Editor.IsAvailable)
        {
            Log.Warn($"The editor is not available: {host.Editor.UnavailableReason ?? "unknown reason"}. The file is {info.Path}");
            return;
        }

        host.Editor.Open(info.Name, info.Path);
        Log.Info($"Opening '{info.Name}' in the editor.");
    }

    private static void ReportState(ScriptingHost host, string verb, string name)
    {
        var info = host.Catalog.Get(name);
        if (info is null)
        {
            Log.Info($"script {verb}: '{name}' is no longer in the catalog.");
            return;
        }

        var text = $"script {verb}: '{info.Name}' is {info.State}" + (info.LastError is null ? "" : $" ({info.LastError})");
        if (info.State is ScriptState.Error or ScriptState.Disabled)
            Log.Warn(text);
        else
            Log.Info(text);
    }

    private static void List(ScriptingHost host)
    {
        host.Catalog.Scan();
        var scripts = host.Catalog.All();
        if (scripts.Count == 0)
        {
            Log.Info($"No scripts found in {host.Catalog.ScriptsDir}.");
            return;
        }

        var nameWidth = Math.Max(4, scripts.Max(s => s.Name.Length));
        var languageWidth = Math.Max(8, scripts.Max(s => s.Language.Length));
        var text = new StringBuilder();
        text.Append("Scripts in ").Append(host.Catalog.ScriptsDir).Append(':');
        text.Append('\n').Append("NAME".PadRight(nameWidth)).Append("  ").Append("LANGUAGE".PadRight(languageWidth)).Append("  ").Append("STATE".PadRight(8)).Append("  LAST ERROR");
        foreach (var script in scripts)
        {
            text.Append('\n')
                .Append(script.Name.PadRight(nameWidth)).Append("  ")
                .Append(script.Language.PadRight(languageWidth)).Append("  ")
                .Append(script.State.ToString().PadRight(8)).Append("  ")
                .Append(script.LastError?.ReplaceLineEndings(" ") ?? "");
        }

        Log.Info(text.ToString());
    }

    private static void Stubs(ScriptingHost host)
    {
        var written = StubWriter.WriteAll(host.Paths.ScriptsDir);
        Log.Info(written.Count == 0
            ? $"The editor stubs in {host.Paths.ScriptsDir} are up to date."
            : $"Wrote {written.Count} editor stub file(s) in {host.Paths.ScriptsDir}: {string.Join(", ", written.Select(Path.GetFileName))}");
    }

    private static void Events(ScriptingHost host)
    {
        var counters = host.Events.Counters;
        var width = EventCatalog.All.Max(e => e.Name.Length);
        var text = new StringBuilder("Script events (published so far):");
        foreach (var ev in EventCatalog.All)
        {
            counters.TryGetValue(ev.Name, out var count);
            text.Append('\n').Append(ev.Name.PadRight(width)).Append("  ").Append(ev.Producer.PadRight(8)).Append("  ").Append(count);
        }

        Log.Info(text.ToString());
    }

    private static void Call(ScriptingHost host, string rest)
    {
        SplitFirst(rest, out var command, out var arguments);
        if (command.Length == 0)
        {
            Log.Info("usage: script call <command> [args]");
            return;
        }

        host.Post(() => Guarded("call", () =>
        {
            if (!host.Scheduler.InvokeCommand(command, arguments))
                Log.Warn($"No script command '{command}' is registered (scripts register them with ksa.command).");
        }));
    }

    // ---------------------------------------------------------------- helpers

    private static void Guarded(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn($"script {what} failed: {ex.Message}");
        }
    }

    private static void SplitFirst(string text, out string first, out string rest)
    {
        text = text.Trim();
        var space = text.IndexOfAny([' ', '\t']);
        if (space < 0)
        {
            first = text;
            rest = "";
            return;
        }

        first = text[..space];
        rest = text[(space + 1)..].Trim();
    }
}
