using System.Reflection;
using Brutal.ImGuiApi;
using HarmonyLib;
using KSA;
using KsaScripting.Core.Logging;
using KsaScripting.Ksa;

namespace KsaScripting.GameMod.UI;

/// <summary>Harmony patches owned by the UI: the hotkey guard and the fallback menu.</summary>
public static class UiPatches
{
    /// <summary>Applies each UI patch class independently (one failure never blocks the others).</summary>
    public static void Install(Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        Apply(harmony, typeof(HotkeyGuardPatch), required: true);
        Apply(harmony, typeof(MenuFallbackPatch), required: false);
    }

    private static void Apply(Harmony harmony, Type type, bool required)
    {
        try
        {
            harmony.CreateClassProcessor(type).Patch();
            Log.Info($"UI patch '{type.Name}' applied.");
        }
        catch (Exception ex)
        {
            string consequence = required
                ? "typing in the Script Manager's text fields may also trigger game hotkeys"
                : "the Scripting menu is missing; use the toggle hotkey or ModMenu";
            Log.Warn($"UI patch '{type.Name}' failed to apply ({consequence}): {ex.Message}");
        }
    }
}

/// <summary>
///     Blocks game hotkeys (<c>GameSettings.OnKeyAll</c>) while a text input inside the Script Manager has
///     keyboard focus, so typing a script name does not toggle the console, the map, staging, ... The gate is
///     the mod-scoped <see cref="ScriptManagerWindow.WantsTextInput"/> flag (set per frame inside Begin/End),
///     never <c>ImGui.GetIO().WantTextInput</c> alone: that is global and also true for other mods' fields
///     and the game's own console. The in-game console is exempt so it keeps working.
/// </summary>
[HarmonyPatch(typeof(GameSettings), nameof(GameSettings.OnKeyAll))]
internal static class HotkeyGuardPatch
{
    [KsaAnchor("GameSettings.OnKeyAll(GlfwKeyEvent); Program.ConsoleWindow.IsOpen",
        SourceFile = "KSA/GameSettings.cs / KSA/Program.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Low,
        Notes = "OnKeyAll heads the input chain (GameSettings.OnKeyAll -> Popup.OnKeyAll -> ConsoleWindow.OnKey -> ...): "
            + "returning true from this prefix (with __result = true) swallows the key before every game handler, "
            + "which is also why the open console must be excluded. Runs on the GLFW poll thread: the flag is volatile.")]
    [HarmonyPrefix]
    private static bool Prefix(ref bool __result)
    {
        try
        {
            // Program.ConsoleWindow can be unassigned very early in startup; a null/closed console counts as "not open".
            bool consoleOpen = Program.ConsoleWindow is { IsOpen: true };
            if (!consoleOpen && ScriptManagerWindow.WantsTextInput)
            {
                __result = true;
                return false;
            }
        }
        catch (Exception)
        {
            // Never let the guard break the game's input chain.
        }

        return true;
    }
}

/// <summary>
///     Draws the fallback top-level "Scripting" game menu through the <c>Program.DrawProgramMenusHook()</c>
///     extension point KSA calls at the end of its menu bar (right after the View menu). Used only when the
///     ModMenu companion mod is absent (with ModMenu the <c>[ModMenuEntry]</c> path draws the same content).
///     A postfix on that empty public hook replaces a fragile IL transpiler.
/// </summary>
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class MenuFallbackPatch
{
    private static bool? s_modMenuPresent;

    [KsaAnchor("Program.DrawProgramMenusHook(); ModLibrary.Find(string); Program.MainViewport; IGameViewportLifecycle.SetMenuBarInUse(bool)",
        SourceFile = "KSA/Program.cs / KSA/ModLibrary.cs / KSA/IGameViewportLifecycle.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "DrawProgramMenusHook is an empty instance hook called inside the open menu bar after the View/HUD menus. "
            + "Since 5402 IGameViewport.MenuBarInUse is read-only; the setter is the explicit IGameViewportLifecycle.SetMenuBarInUse "
            + "(the call DrawMenuBar itself makes). Same pattern as purrTTY's Patch02.")]
    [HarmonyPostfix]
    private static void Postfix()
    {
        try
        {
            if (IsModMenuPresent())
            {
                return;
            }

            if (ImGui.BeginMenu("Scripting"))
            {
                try
                {
                    // Keep the (possibly auto-hidden) menu bar shown, and the game's hotkeys/camera quiet, while the menu is open.
                    ((IGameViewportLifecycle)Program.MainViewport).SetMenuBarInUse(true);
                    ScriptManagerWindow.DrawMenuContent();
                }
                finally
                {
                    ImGui.EndMenu();
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("ui.menu.fallback", LogLevel.Warn, $"The Scripting menu failed to draw (logged once): {ex.Message}");
        }
    }

    // ModLibrary.Find(string) is reached by reflection: its sibling overload Find(KeyHash) lives in Planet.Render.Core,
    // which this project does not reference, so a compile-time call fails overload resolution (CS0012).
    private static readonly MethodInfo? FindMod = typeof(ModLibrary).GetMethod(
        nameof(ModLibrary.Find),
        BindingFlags.Public | BindingFlags.Static,
        binder: null,
        types: [typeof(string)],
        modifiers: null);

    private static bool IsModMenuPresent() => s_modMenuPresent ??= ProbeModMenu();

    private static bool ProbeModMenu()
    {
        try
        {
            if (FindMod is not null)
            {
                return FindMod.Invoke(null, ["ModMenu"]) is not null;
            }

            return AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, "ModMenu", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Log.LogOnce("ui.menu.probe", LogLevel.Warn, $"Probing for ModMenu failed (assuming it is absent): {ex.Message}");
            return false;
        }
    }
}
