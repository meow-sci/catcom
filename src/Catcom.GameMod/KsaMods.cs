using System.Reflection;
using KSA;
using Catcom.Core.Logging;
using Catcom.Ksa;

namespace Catcom.GameMod;

/// <summary>Asks the game's mod library which mods are loaded.</summary>
internal static class KsaMods
{
    // ModLibrary.Find(string) is reached by reflection: its sibling overload Find(KeyHash) lives in Planet.Render.Core,
    // which this project does not reference, so a compile-time call fails overload resolution (CS0012).
    private static readonly MethodInfo? FindMod = typeof(ModLibrary).GetMethod(
        nameof(ModLibrary.Find),
        BindingFlags.Public | BindingFlags.Static,
        binder: null,
        types: [typeof(string)],
        modifiers: null);

    /// <summary>True when the game's mod library knows a loaded mod with this id. Never throws; any failure reads as "not loaded".</summary>
    /// <param name="modId">The mod id as written in <c>mod.toml</c> (<c>ModMenu</c>, <c>purrTTY</c>, ...).</param>
    /// <returns>Whether the mod is loaded.</returns>
    [KsaAnchor("ModLibrary.Find(string)", SourceFile = "KSA/ModLibrary.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "Reached by reflection (the Find(KeyHash) overload needs Planet.Render.Core). When the method cannot be found, "
            + "the already-loaded assemblies are scanned for the mod id as an assembly name instead.")]
    public static bool IsLoaded(string modId)
    {
        try
        {
            if (FindMod is not null)
            {
                return FindMod.Invoke(null, [modId]) is not null;
            }

            return AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, modId, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Log.LogOnce("mods.probe." + modId, LogLevel.Warn, $"Probing for the '{modId}' mod failed (assuming it is absent): {ex.Message}");
            return false;
        }
    }
}
