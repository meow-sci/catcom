using System.Text;
using Catcom.Core.Hooks;
using Catcom.Core.Runtime;

namespace Catcom.GameMod.UI;

/// <summary>Which starter file the New dialog writes.</summary>
internal enum ScriptTemplateKind
{
    /// <summary>Runs once from Run and exits; prints the active vessel's telemetry.</summary>
    AdHoc,

    /// <summary>Exports <c>onEvent(ctx)</c> (Lua <c>on_event</c>) to attach to an event.</summary>
    EventHandler,

    /// <summary>Exports <c>main(ctx)</c> as a loop that runs until the script is stopped.</summary>
    ServiceLoop,

    /// <summary>A minimal full-screen TUI on the script's own terminal (the shipped <c>lib/tui</c> helpers).</summary>
    TerminalApp,
}

/// <summary>
///     The starter files written by the Script Manager's New dialog: four templates in two languages. Each
///     is checked against the shipped API stubs (<c>host.d.ts</c> with <c>tsc --noEmit</c>, LuaJIT bytecode
///     compile) — keep them in step with the API schema and with <c>scripts-lib/tui.*</c>.
/// </summary>
internal static class ScriptTemplates
{
    /// <summary>Events listed in the event-handler templates' comment block (names that are not in the catalog are skipped).</summary>
    private static readonly string[] ShowcaseEvents =
    [
        "stage", "engine.ignite", "situation.change", "soi.change", "dock", "undock", "vessel.destroy", "warp.change",
    ];

    /// <summary>The templates in display order with the text the New dialog shows for each.</summary>
    public static IReadOnlyList<(ScriptTemplateKind Kind, string Label, string Description)> All { get; } =
    [
        (ScriptTemplateKind.AdHoc, "Ad-hoc",
            "Runs once when you press Run: prints the active vessel's name, situation, altitude and mass."),
        (ScriptTemplateKind.EventHandler, "Event handler",
            "Exports a function to attach to a game event (Bindings tab > Add binding), with the event names listed."),
        (ScriptTemplateKind.ServiceLoop, "Service loop",
            "main(ctx) loops until you press Stop, sleeping between iterations."),
        (ScriptTemplateKind.TerminalApp, "Terminal app",
            "A small full-screen app on a purrTTY terminal: a button that toggles a light. Press q to quit."),
    ];

    /// <summary>The file extension (with the dot) of a language.</summary>
    /// <param name="language"><see cref="ScriptLanguages.Lua"/> or <see cref="ScriptLanguages.TypeScript"/>.</param>
    public static string Extension(string language)
        => language == ScriptLanguages.TypeScript ? ".ts" : ".lua";

    /// <summary>Renders a template.</summary>
    /// <param name="kind">The template.</param>
    /// <param name="language"><see cref="ScriptLanguages.Lua"/> or <see cref="ScriptLanguages.TypeScript"/>.</param>
    /// <param name="scriptName">The catalog name (path under the scripts folder, with <c>/</c>, without extension); its base name becomes the header title and its folder depth the relative import of <c>lib/tui</c>.</param>
    public static string Render(ScriptTemplateKind kind, string language, string scriptName)
    {
        bool ts = language == ScriptLanguages.TypeScript;
        string title = BaseName(scriptName);
        string text = (kind, ts) switch
        {
            (ScriptTemplateKind.AdHoc, true) => TsAdHoc,
            (ScriptTemplateKind.AdHoc, false) => LuaAdHoc,
            (ScriptTemplateKind.EventHandler, true) => TsEvent,
            (ScriptTemplateKind.EventHandler, false) => LuaEvent,
            (ScriptTemplateKind.ServiceLoop, true) => TsService,
            (ScriptTemplateKind.ServiceLoop, false) => LuaService,
            (ScriptTemplateKind.TerminalApp, true) => TsTerminal,
            _ => LuaTerminal,
        };

        return text
            .Replace("{{TITLE}}", title, StringComparison.Ordinal)
            .Replace("{{EVENTS}}", EventList(ts ? "//" : "--"), StringComparison.Ordinal)
            .Replace("{{TUI}}", TuiImport(scriptName), StringComparison.Ordinal)
            + "\n";
    }

    private static string BaseName(string scriptName)
    {
        int slash = scriptName.LastIndexOf('/');
        string name = slash >= 0 ? scriptName[(slash + 1)..] : scriptName;
        return name.Replace("\"", "", StringComparison.Ordinal);
    }

    /// <summary>The TypeScript specifier of <c>lib/tui</c> relative to a script at <paramref name="scriptName"/>.</summary>
    internal static string TuiImport(string scriptName)
    {
        int depth = scriptName.Count(c => c == '/');
        return depth == 0 ? "./lib/tui" : string.Concat(Enumerable.Repeat("../", depth)) + "lib/tui";
    }

    private static string EventList(string comment)
    {
        var sb = new StringBuilder();
        foreach (string name in ShowcaseEvents)
        {
            if (EventCatalog.Find(name) is not { } ev)
            {
                continue;
            }

            string description = ev.Description;
            int cut = description.IndexOfAny(['.', ';']);
            if (cut > 0)
            {
                description = description[..cut];
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(comment).Append("   ").Append(name.PadRight(17)).Append(' ').Append(description.Trim());
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------ TypeScript

    private const string TsAdHoc =
        """
        // @ksa name="{{TITLE}}"
        // Ad-hoc script: press Run in the Script Manager and main() runs once, then the script is done.
        // API reference: scripts/stubs/host.d.ts (type hints and docs appear in the editor).

        export function main(ctx: ksa.MainContext): void {
          const vessel = ksa.active;
          if (!vessel) {
            ksa.console.warn("No active vessel.");
            return;
          }

          ksa.console.log(
            `${vessel.name}: ${vessel.situation}, altitude ${vessel.altitude.barometric.toFixed(0)} m, mass ${vessel.mass.total.toFixed(0)} kg`,
          );
        }
        """;

    private const string TsEvent =
        """
        // @ksa name="{{TITLE}}"
        // Event handler: onEvent runs whenever the event it is attached to fires.
        // Attach it in the Script Manager (Bindings tab > Add binding...), or in code with ksa.on (see below).
        // API reference: scripts/stubs/host.d.ts.
        //
        // A few of the events you can attach to (the Script Manager's Events tab lists all of them):
        {{EVENTS}}

        export function onEvent(ctx: ksa.EventContext): void {
          ksa.console.log(`${ctx.type} on vessel ${ctx.vesselId ?? "(none)"}`);
        }

        // To attach it from code instead of the Bindings tab, uncomment:
        // ksa.on("stage", onEvent);
        """;

    private const string TsService =
        """
        // @ksa name="{{TITLE}}"
        // Service: Start runs main() in the background until you press Stop (or reload the script).
        // ctx.signal.cancelled turns true when that happens; ksa.sleep(1) waits one second of game time
        // (pass "wall" as the second argument to wait real time even while the game is paused).
        // API reference: scripts/stubs/host.d.ts.

        export async function main(ctx: ksa.ServiceContext): Promise<void> {
          while (!ctx.signal.cancelled) {
            const vessel = ksa.active;
            if (vessel) {
              ksa.console.log(`${vessel.name}: ${vessel.altitude.barometric.toFixed(0)} m, ${vessel.velocity.surface.toFixed(1)} m/s`);
            }

            await ksa.sleep(1);
          }
        }
        """;

    private const string TsTerminal =
        """
        // @ksa name="{{TITLE}}" terminal=true
        // Terminal app: the header above gives the script its own terminal. Start it, then open its terminal
        // from the Script Manager (Terminal tab > Open 2D window, or Place on part...).
        // Click the button to toggle the first light of the active vessel; press q to quit.
        // The tui helpers live in scripts/lib/tui.ts (see scripts/lib/README.md).
        import { Screen, parseInput, Button } from "{{TUI}}";

        function toggleLight(): string {
          const light = ksa.active?.lights[0];
          if (!light) {
            return "no light on the active vessel";
          }

          const want = !light.on;
          light.setOn(want);
          return want ? "light switched ON" : "light switched OFF";
        }

        export async function main(ctx: ksa.TerminalContext): Promise<void> {
          const term = ctx.term;
          const screen = new Screen(term);
          const button = new Button({ x: 3, y: 4, w: 20, label: "TOGGLE LIGHT" });
          let status = "click the button";

          const draw = (): void => {
            const { cols, rows } = term.size;
            screen.frame(() => {
              screen.clear();
              screen.box(1, 1, cols, rows, "Light switch");
              button.draw(screen);
              screen.text(3, 6, status, { fg: "green" });
              screen.text(3, Math.max(rows - 1, 2), "click the button - press q to quit", { dim: true });
            });
          };

          screen.enterApp({ mouse: "press-release" });
          const resized = term.on("resize", draw);
          draw();
          try {
            for await (const ev of parseInput(term)) {
              if (ev.kind === "mouse" && button.hit(ev)) {
                status = toggleLight();
                draw();
              } else if (ev.kind === "key" && (ev.name === "q" || (ev.name === "c" && ev.mods.ctrl))) {
                break;
              }
            }
          } finally {
            resized.unsubscribe();
            screen.leaveApp();
          }
        }
        """;

    // ------------------------------------------------------------------------------------ Lua

    private const string LuaAdHoc =
        """
        -- @ksa name="{{TITLE}}"
        -- Ad-hoc script: press Run in the Script Manager and main() runs once, then the script is done.
        -- API reference: scripts/stubs/ksa.lua (type hints and docs appear in the editor).

        return {
          main = function(ctx)
            local vessel = ksa.active
            if not vessel then
              ksa.console.warn("No active vessel.")
              return
            end

            ksa.console.log(string.format("%s: %s, altitude %.0f m, mass %.0f kg",
              vessel.name, vessel.situation, vessel.altitude.barometric, vessel.mass.total))
          end,
        }
        """;

    private const string LuaEvent =
        """
        -- @ksa name="{{TITLE}}"
        -- Event handler: on_event runs whenever the event it is attached to fires.
        -- Attach it in the Script Manager (Bindings tab > Add binding...), or in code with ksa.on (see below).
        -- API reference: scripts/stubs/ksa.lua.
        --
        -- A few of the events you can attach to (the Script Manager's Events tab lists all of them):
        {{EVENTS}}

        return {
          on_event = function(ctx)
            ksa.console.log(ctx.type .. " on vessel " .. tostring(ctx.vessel_id or "(none)"))
          end,
        }

        -- To attach it from code instead of the Bindings tab, register it before returning:
        --   ksa.on("stage", function(ctx) ksa.console.log("staged") end)
        """;

    private const string LuaService =
        """
        -- @ksa name="{{TITLE}}"
        -- Service: Start runs main() in the background until you press Stop (or reload the script).
        -- ctx.signal.cancelled turns true when that happens; ksa.sleep(1) waits one second of game time
        -- (pass "wall" as the second argument to wait real time even while the game is paused).
        -- API reference: scripts/stubs/ksa.lua.

        return {
          main = function(ctx)
            while not ctx.signal.cancelled do
              local vessel = ksa.active
              if vessel then
                ksa.console.log(string.format("%s: %.0f m, %.1f m/s",
                  vessel.name, vessel.altitude.barometric, vessel.velocity.surface))
              end

              ksa.sleep(1)
            end
          end,
        }
        """;

    private const string LuaTerminal =
        """
        -- @ksa name="{{TITLE}}" terminal=true
        -- Terminal app: the header above gives the script its own terminal. Start it, then open its terminal
        -- from the Script Manager (Terminal tab > Open 2D window, or Place on part...).
        -- Click the button to toggle the first light of the active vessel; press q to quit.
        -- The tui helpers live in scripts/lib/tui.lua (see scripts/lib/README.md).
        local tui = require("lib.tui")

        local function toggle_light()
          local vessel = ksa.active
          local light = vessel and vessel.lights[1]
          if not light then
            return "no light on the active vessel"
          end

          local want = not light.on
          light:set_on(want)
          return want and "light switched ON" or "light switched OFF"
        end

        return {
          main = function(ctx)
            local term = ctx.term
            local screen = tui.Screen.new(term)
            local button = tui.Button.new({ x = 3, y = 4, w = 20, label = "TOGGLE LIGHT" })
            local status = "click the button"

            local function draw()
              local cols, rows = term.size.cols, term.size.rows
              screen:frame(function()
                screen:clear()
                screen:box(1, 1, cols, rows, "Light switch")
                button:draw(screen)
                screen:text(3, 6, status, { fg = "green" })
                screen:text(3, math.max(rows - 1, 2), "click the button - press q to quit", { dim = true })
              end)
            end

            screen:enter_app({ mouse = "press-release" })
            local resized = term.on("resize", draw)
            draw()
            for ev in tui.parse_input(term) do
              if ev.kind == "mouse" and button:hit(ev) then
                status = toggle_light()
                draw()
              elseif ev.kind == "key" and (ev.name == "q" or (ev.name == "c" and ev.mods.ctrl)) then
                break
              end
            end

            resized.unsubscribe()
            screen:leave_app()
          end,
        }
        """;
}
