using HarmonyLib;
using KsaScripting.Core.Logging;
using KsaScripting.GameMod.UI;
using ModMenu;
using StarMap.API;

namespace KsaScripting.GameMod;

/// <summary>
///     The StarMap entry point of ksa-scripting. Every lifecycle method is guarded: a failure of the
///     scripting host must never break game start-up, a frame, or shutdown. When
///     <see cref="ScriptingHost.Start"/> fails the mod stays idle (the error is logged once) and no later
///     hook touches a half-built host.
/// </summary>
[StarMapMod]
public sealed class Mod
{
    private const string UiHarmonyId = "ksa-scripting.ui";

    private ScriptingHost? _host;
    private ScriptManagerWindow? _window;
    private Harmony? _uiHarmony;

    /// <summary>StarMap contract: ksa-scripting unloads in the normal (late) phase.</summary>
    public bool ImmediateUnload => false;

    /// <summary>The renderer is not live yet; only installs the log sink and logs the version.</summary>
    /// <param name="definingMod">The KSA mod definition of ksa-scripting.</param>
    [StarMapImmediateLoad]
    public void OnImmediateLoad(KSA.Mod definingMod)
    {
        try
        {
            Log.SetSink(new ModLogSink());
            var name = string.IsNullOrEmpty(definingMod?.Name) ? "ksa-scripting" : definingMod.Name;
            Log.Info($"{name} {ModInfo.Version} loaded.");
        }
        catch (Exception ex)
        {
            Log.LogOnce("mod.immediateload", LogLevel.Error, $"OnImmediateLoad failed: {ex}");
        }
    }

    /// <summary>
    ///     Builds the scripting host (config, runtimes, bridge, slots, event patches, commands, shells,
    ///     catalog scan + autostart) and the Script Manager UI.
    /// </summary>
    [StarMapAllModsLoaded]
    public void OnAllModsLoaded()
    {
        if (_host is not null)
            return;

        try
        {
            _host = ScriptingHost.Start(ModPaths.Detect());
        }
        catch (Exception ex)
        {
            // ScriptingHost.Start already logged the failure and reset the log sink while tearing down.
            _host = null;
            try
            {
                Log.SetSink(new ModLogSink());
                Log.Error($"ksa-scripting stays idle: {ex.Message}");
            }
            catch
            {
                // Nothing left to report to.
            }

            return;
        }

        try
        {
            _window = new ScriptManagerWindow(_host);
        }
        catch (Exception ex)
        {
            _window = null;
            Log.LogOnce("mod.window", LogLevel.Error, $"The Script Manager window could not be created: {ex}");
        }

        try
        {
            _uiHarmony = new Harmony(UiHarmonyId);
            UiPatches.Install(_uiHarmony);
        }
        catch (Exception ex)
        {
            Log.LogOnce("mod.uipatches", LogLevel.Error, $"The UI patches could not be installed: {ex}");
        }
    }

    /// <summary>Per-frame hook before the GUI; nothing to do (script work runs in the Step slot).</summary>
    /// <param name="dt">The frame time in seconds.</param>
    [StarMapBeforeGui]
    public void OnBeforeGui(double dt)
    {
        _ = dt;
    }

    /// <summary>Draws the Script Manager window and pumps the editor sessions.</summary>
    /// <param name="dt">The frame time in seconds.</param>
    [StarMapAfterGui]
    public void OnAfterGui(double dt)
    {
        var host = _host;
        if (host is null)
            return;

        try
        {
            _window?.Draw(dt);
        }
        catch (Exception ex)
        {
            Log.LogOnce("mod.draw", LogLevel.Error, $"The Script Manager window failed: {ex}");
        }

        try
        {
            host.Editor.Pump();
        }
        catch (Exception ex)
        {
            Log.LogOnce("mod.editorpump", LogLevel.Error, $"The editor pump failed: {ex}");
        }
    }

    /// <summary>The F2-proof frame latch: re-runs the Step slot on frames where the solver postfix did not fire.</summary>
    /// <param name="currentPlayerTime">The wall-clock player time in seconds.</param>
    /// <param name="dtPlayer">The wall-clock frame time in seconds.</param>
    [StarMapAfterOnFrame]
    public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
    {
        var host = _host;
        if (host is null)
            return;

        try
        {
            host.Slots.OnAfterFrame(dtPlayer);
        }
        catch (Exception ex)
        {
            Log.LogOnce("mod.afterframe", LogLevel.Error, $"The frame latch failed: {ex}");
        }
    }

    /// <summary>Reverse teardown: UI patches first, then the whole scripting host.</summary>
    [StarMapUnload]
    public void OnUnload()
    {
        try
        {
            _uiHarmony?.UnpatchAll(UiHarmonyId);
        }
        catch (Exception ex)
        {
            Log.LogOnce("mod.uiunpatch", LogLevel.Warn, $"Removing the UI patches failed: {ex.Message}");
        }

        _uiHarmony = null;
        _window = null;

        var host = _host;
        _host = null;
        if (host is null)
            return;

        try
        {
            host.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogOnce("mod.unload", LogLevel.Error, $"Unloading ksa-scripting failed: {ex}");
        }
    }

    /// <summary>The "Scripting" entry of the ModMenu; also drawn by the fallback menu patch.</summary>
    [ModMenuEntry("Scripting")]
    public static void DrawMenu()
    {
        try
        {
            ScriptManagerWindow.DrawMenuContent();
        }
        catch (Exception ex)
        {
            Log.LogOnce("mod.menu", LogLevel.Error, $"The Scripting menu failed: {ex}");
        }
    }
}

/// <summary>The mod's own version, read from the assembly (shown in the log and in the shell metadata).</summary>
internal static class ModInfo
{
    /// <summary>The assembly version, or 0.1.0 when unavailable.</summary>
    public static Version AssemblyVersion
    {
        get
        {
            try
            {
                return typeof(ModInfo).Assembly.GetName().Version ?? new Version(0, 1, 0);
            }
            catch
            {
                return new Version(0, 1, 0);
            }
        }
    }

    /// <summary>The informational version without the source-control suffix, e.g. <c>0.1.0</c>.</summary>
    public static string Version
    {
        get
        {
            try
            {
                var informational = typeof(ModInfo).Assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational))
                {
                    var plus = informational.IndexOf('+');
                    return plus > 0 ? informational[..plus] : informational;
                }
            }
            catch
            {
                // Fall through to the assembly version.
            }

            var version = AssemblyVersion;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}
