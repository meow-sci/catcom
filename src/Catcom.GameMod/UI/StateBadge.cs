using Brutal.ImGuiApi;
using Brutal.Numerics;
using Catcom.Core.Catalog;

namespace Catcom.GameMod.UI;

/// <summary>The short coloured state tag shown for a script: IDLE, RUN, SVC, TERM, ERR, OFF (and LOAD while loading).</summary>
internal static class StateBadge
{
    private static readonly float4 IdleColor = new(0.62f, 0.62f, 0.66f, 1f);
    private static readonly float4 RunColor = new(0.45f, 0.9f, 0.5f, 1f);
    private static readonly float4 ServiceColor = new(0.45f, 0.78f, 1f, 1f);
    private static readonly float4 TerminalColor = new(0.85f, 0.6f, 1f, 1f);
    private static readonly float4 ErrorColor = new(1f, 0.35f, 0.35f, 1f);
    private static readonly float4 DisabledColor = new(1f, 0.6f, 0.2f, 1f);
    private static readonly float4 LoadingColor = new(1f, 0.85f, 0.3f, 1f);

    /// <summary>The badge text and colour of a script.</summary>
    /// <param name="info">The catalog entry.</param>
    /// <param name="terminalAttached">Whether a purrTTY surface is attached to the script's terminal.</param>
    public static (string Text, float4 Color) For(ScriptInfo info, bool terminalAttached)
    {
        return info.State switch
        {
            ScriptState.Idle => ("IDLE", IdleColor),
            ScriptState.Loading => ("LOAD", LoadingColor),
            ScriptState.Running => terminalAttached ? ("TERM", TerminalColor) : ("RUN", RunColor),
            ScriptState.Service => terminalAttached ? ("TERM", TerminalColor) : ("SVC", ServiceColor),
            ScriptState.Error => ("ERR", ErrorColor),
            ScriptState.Disabled => ("OFF", DisabledColor),
            _ => ("?", IdleColor),
        };
    }

    /// <summary>A longer description of the state for tooltips.</summary>
    public static string Describe(ScriptInfo info, bool terminalAttached)
    {
        return info.State switch
        {
            ScriptState.Idle => "Idle: known on disk, not loaded.",
            ScriptState.Loading => "Loading: being read, compiled and evaluated.",
            ScriptState.Running => terminalAttached
                ? "Terminal: loaded, with a purrTTY surface attached to its terminal."
                : "Running: loaded; its hooks and bindings are active.",
            ScriptState.Service => terminalAttached
                ? "Terminal: running main(ctx) as a service, with a purrTTY surface attached."
                : "Service: loaded and running main(ctx) until stopped.",
            ScriptState.Error => "Error: the last load or start failed (see the last error).",
            ScriptState.Disabled => "Disabled: latched off after repeated faults or a watchdog trip; Reload to re-enable.",
            _ => "",
        };
    }

    /// <summary>Draws the badge as coloured text.</summary>
    public static void Draw(ScriptInfo info, bool terminalAttached)
    {
        (string text, float4 color) = For(info, terminalAttached);
        ImGuiWidgets.TextColored(color, text);
    }
}
