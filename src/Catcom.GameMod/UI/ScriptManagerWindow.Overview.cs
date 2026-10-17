using System.Globalization;
using System.Text.RegularExpressions;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Catcom.Core.Catalog;
using Catcom.Core.Hooks;
using Catcom.Core.Hosting;
using Catcom.Core.Runtime;

namespace Catcom.GameMod.UI;

// Overview tab: what the catalog knows about the selected script, its declared hooks, errors and runtime stats.
public sealed partial class ScriptManagerWindow
{
    // "path/to/file.lua:12: message", "...file.ts:3:5: message", "C:\x\y.ts:7:1 ..."
    private static readonly Regex ErrorLocation = new(
        @"(?<file>(?:[A-Za-z]:)?[^\s:""'<>|*?]+?\.(?:lua|ts)):(?<line>\d+)(?::(?<col>\d+))?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    private void DrawOverview(ScriptInfo info)
    {
        IScriptRuntime? runtime = _host.Runtimes.ForLanguage(info.Language);

        // A failed script's error is the first thing to read.
        DrawLastError(info);

        if (ImGuiWidgets.BeginFormTableFixed("##ov_form", 100f, 2f))
        {
            try
            {
                ImGuiWidgets.FormText("Path", info.Path);
                ImGuiWidgets.FormText("Language", info.Language == ScriptLanguages.TypeScript ? "TypeScript" : "Lua");

                if (runtime is null)
                {
                    ImGuiWidgets.FormText("Runtime", "no runtime registered for this language", ImGuiWidgets.ErrorColor);
                }
                else if (!runtime.IsAvailable)
                {
                    ImGuiWidgets.FormText("Runtime", $"{runtime.Id} - unavailable: {runtime.UnavailableReason ?? "unknown reason"}", ImGuiWidgets.ErrorColor);
                }
                else
                {
                    ImGuiWidgets.FormText("Runtime", $"{runtime.Id} - available", ImGuiWidgets.SuccessColor);
                }

                bool terminalAttached = HasAttachedTerminal(info.Name);
                (string badge, float4 badgeColor) = StateBadge.For(info, terminalAttached);
                ImGuiWidgets.FormText("State", $"{badge}  {StateBadge.Describe(info, terminalAttached)}", badgeColor);

                ImGuiWidgets.FormText("Title", info.Title ?? "(none - set with  @ksa name=\"...\"  in the header)");
                bool inConfig = _host.Config.Autostart.Contains(info.Name);
                ImGuiWidgets.FormText(
                    "Autostart",
                    $"header: {(info.Autostart ? "yes" : "no")}   config list: {(inConfig ? "yes" : "no")}");
                ImGuiWidgets.FormText(
                    "Terminal",
                    info.HasTerminal ? "yes - header says terminal=true (gets ctx.term when started)" : "no - add  @ksa terminal=true  to the header to give it a terminal");
                ImGuiWidgets.FormText(
                    "Exports",
                    info.Exports.Count > 0 ? string.Join(", ", info.Exports) : "(none known - Run, Start or Reload the script to discover them)");
            }
            finally
            {
                ImGuiWidgets.EndFormTable();
            }
        }

        DrawDeclaredHooks(info);
        DrawRuntimeStats(runtime);
    }

    private void DrawLastError(ScriptInfo info)
    {
        if (info.LastError is not { Length: > 0 } error)
        {
            return;
        }

        ImGui.SeparatorText("Last error");
        using (new IdScope("lasterror"))
        {
            float wrap = Math.Max(120f, ImGui.GetContentRegionAvail().X - 24f);
            float height = Math.Clamp(ImGui.CalcTextSize(error, false, wrap).Y + 20f, 44f, 160f);
            bool visible = ImGui.BeginChild("##lasterr_text", new float2(0f, height), ImGuiChildFlags.Borders);
            try
            {
                if (visible)
                {
                    ImGuiWidgets.TextColoredWrapped(ImGuiWidgets.ErrorColor, error);
                }
            }
            finally
            {
                ImGui.EndChild();
            }

            if (TryParseErrorLocation(info, error, out string path, out int line))
            {
                if (ImGuiWidgets.Button("Jump in editor", _host.Editor.IsAvailable, _host.Editor.UnavailableReason ?? "The editor is not available."))
                {
                    string name = _host.Catalog.NameForPath(path);
                    OpenEditor(string.IsNullOrEmpty(name) || name.StartsWith("..", StringComparison.Ordinal) ? info.Name : name, path, line);
                }

                ImGui.SameLine();
            }

            if (ImGui.Button("Copy error"))
            {
                ImGui.SetClipboardText(error);
            }
        }
    }

    /// <summary>
    ///     Finds the first <c>file:line[:col]</c> in an error message and resolves the file against the scripts
    ///     folder and the data folder (Lua chunk names are <c>scripts/&lt;relative&gt;</c>); falls back to the
    ///     script itself when the named file cannot be found.
    /// </summary>
    private bool TryParseErrorLocation(ScriptInfo info, string error, out string path, out int line)
    {
        path = info.Path;
        line = 0;
        try
        {
            Match match = ErrorLocation.Match(error);
            if (!match.Success || !int.TryParse(match.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out line))
            {
                return false;
            }

            string file = match.Groups["file"].Value.Replace('/', Path.DirectorySeparatorChar);
            string scriptsRoot = Path.GetFullPath(_host.Paths.ScriptsDir);
            string[] candidates =
            [
                file,
                Path.Combine(_host.Paths.DataDir, file),
                Path.Combine(scriptsRoot, file),
                Path.Combine(scriptsRoot, Path.GetFileName(file)),
            ];

            foreach (string candidate in candidates)
            {
                if (!Path.IsPathRooted(candidate))
                {
                    continue;
                }

                string full = Path.GetFullPath(candidate);
                if (File.Exists(full) && full.StartsWith(scriptsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    path = full;
                    return true;
                }
            }

            // The named file is not in the scripts folder (prelude, runtime internals): only a hit in the script's own file can be opened.
            return string.Equals(Path.GetFileName(file), Path.GetFileName(info.Path), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private void DrawDeclaredHooks(ScriptInfo info)
    {
        IReadOnlyList<Binding> bindings = _host.Hooks.ForScript(info.Name);
        var declared = new List<Binding>();
        foreach (Binding b in bindings)
        {
            if (b.FromCode)
            {
                declared.Add(b);
            }
        }

        ImGui.SeparatorText("Hooks declared in code");
        if (declared.Count == 0)
        {
            ImGuiWidgets.TextMuted(info.State is ScriptState.Running or ScriptState.Service
                ? "None - the script does not call ksa.on / ksa.every / ksa.service / ksa.command."
                : "None known - hooks are registered when the script loads.");
            return;
        }

        if (ImGui.BeginTable("##ov_hooks", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Borders))
        {
            try
            {
                ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableSetupColumn("Export", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableSetupColumn("Options", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableHeadersRow();

                foreach (Binding b in declared)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(BindingKinds.ToText(b.Kind));
                    ImGui.TableNextColumn();
                    ImGui.Text(b.Key);
                    ImGui.TableNextColumn();
                    ImGui.Text(b.Export);
                    ImGui.TableNextColumn();
                    ImGui.Text(DescribeOptions(b.Kind, b.Key, b.Options));
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }
    }

    private static void DrawRuntimeStats(IScriptRuntime? runtime)
    {
        if (runtime is null || !runtime.IsAvailable)
        {
            return;
        }

        RuntimeStats stats = runtime.Stats;
        ImGui.SeparatorText($"Runtime: {runtime.Id}");
        ImGui.Text($"heap {FormatBytes(stats.HeapBytes)}   last call {stats.LastCallMs:F3} ms   calls {stats.CallsTotal}");
        ImGuiWidgets.TextMuted($"{stats.Version}   JIT {(stats.JitEnabled ? "on" : "off")}   (shared by every script of this language)");
    }

    // ------------------------------------------------------------------------------ shared formatting

    /// <summary>A short human description of a binding's options (sync/vessel for events, interval/clock for ticks).</summary>
    private static string DescribeOptions(BindingKind kind, string key, BindingOptions options)
        => DescribeOptions(BindingKinds.ToText(kind), key, options.Sync, options.Vessel, options.Clock, options.IntervalSeconds);

    private static string DescribeOptions(string kind, string key, bool sync, string? vessel, string clock, double intervalSeconds)
    {
        switch (kind)
        {
            case "event":
                string filter = string.IsNullOrEmpty(vessel) || vessel == "any" ? "any vessel" : $"vessel {vessel}";
                return $"{(sync ? "sync" : "async")}, {filter}";
            case "tick":
                return key == "every"
                    ? $"every {intervalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s ({clock})"
                    : $"each {key} ({clock})";
            case "service":
                return "starts when the script loads";
            default:
                return "";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KiB";
        }

        return $"{bytes / (1024.0 * 1024.0):F1} MiB";
    }
}
