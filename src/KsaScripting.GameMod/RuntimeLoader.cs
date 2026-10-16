using System.Reflection;
using System.Runtime.Loader;
using KsaScripting.Core.Config;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Runtime;

namespace KsaScripting.GameMod;

/// <summary>
///     Finds and instantiates the script runtimes by reflection, so a runtime assembly (or its native
///     library) that is missing or broken degrades that language to "unavailable" instead of failing the
///     whole mod. Nothing here references the runtime assemblies statically.
/// </summary>
internal static class RuntimeLoader
{
    private sealed record Spec(string Id, string Language, string[] TypeNames, string AssemblyName, bool Enabled);

    /// <summary>
    ///     Instantiates every enabled runtime. The result lists the runtimes that exist (an instance whose
    ///     <see cref="IScriptRuntime.IsAvailable"/> is false is kept so the catalog can report its reason) and
    ///     one status row per known runtime (<c>lua</c>, <c>v8</c>) in that order, including disabled and
    ///     missing ones. Never throws.
    /// </summary>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="paths">The mod's folders (the runtime assemblies are looked up there when the normal probe fails).</param>
    /// <returns>The runtimes and their start-up status.</returns>
    public static (IReadOnlyList<IScriptRuntime> Runtimes, List<RuntimeStatus> Statuses) Load(ScriptingConfig config, ModPaths paths)
    {
        var specs = new[]
        {
            new Spec(
                "lua",
                ScriptLanguages.Lua,
                ["KsaScripting.Runtime.Lua.LuaRuntime, KsaScripting.Runtime.Lua"],
                "KsaScripting.Runtime.Lua",
                config.LuaEnabled),

            // The class is named V8ScriptRuntime (V8Runtime is ClearScript's); the short name is tried first
            // so a rename back keeps working.
            new Spec(
                "v8",
                ScriptLanguages.TypeScript,
                [
                    "KsaScripting.Runtime.V8.V8Runtime, KsaScripting.Runtime.V8",
                    "KsaScripting.Runtime.V8.V8ScriptRuntime, KsaScripting.Runtime.V8",
                ],
                "KsaScripting.Runtime.V8",
                config.V8Enabled),
        };

        var runtimes = new List<IScriptRuntime>();
        var statuses = new List<RuntimeStatus>();
        foreach (var spec in specs)
        {
            try
            {
                statuses.Add(LoadOne(spec, paths, runtimes));
            }
            catch (Exception ex)
            {
                // LoadOne catches its own failures; this is the last line of defence.
                var reason = $"runtime failed to load: {ex.Message}";
                Log.Warn($"Runtime '{spec.Id}' ({spec.Language}): {reason}");
                statuses.Add(new RuntimeStatus(spec.Id, spec.Language, false, reason));
            }
        }

        return (runtimes, statuses);
    }

    private static RuntimeStatus LoadOne(Spec spec, ModPaths paths, List<IScriptRuntime> runtimes)
    {
        if (!spec.Enabled)
            return new RuntimeStatus(spec.Id, spec.Language, false, "disabled in ksa-scripting.toml");

        var type = ResolveType(spec, paths, out var typeProblem);
        if (type is null)
        {
            var reason = typeProblem ?? $"runtime not available: type {spec.TypeNames[0]} not found";
            return Unavailable(spec, reason);
        }

        object? instance;
        try
        {
            instance = Activator.CreateInstance(type);
        }
        catch (TargetInvocationException ex)
        {
            return Unavailable(spec, "runtime failed to start: " + (ex.InnerException ?? ex).Message);
        }
        catch (Exception ex)
        {
            // DllNotFoundException, TypeInitializationException, BadImageFormatException, FileLoadException, ...
            return Unavailable(spec, "runtime failed to start: " + ex.Message);
        }

        if (instance is not IScriptRuntime runtime)
        {
            try
            {
                (instance as IDisposable)?.Dispose();
            }
            catch
            {
                // Best effort.
            }

            return Unavailable(spec, $"runtime not available: {type.FullName} is not an IScriptRuntime");
        }

        runtimes.Add(runtime);

        bool available;
        string? unavailableReason;
        try
        {
            available = runtime.IsAvailable;
            unavailableReason = available ? null : runtime.UnavailableReason ?? "runtime not available";
        }
        catch (Exception ex)
        {
            available = false;
            unavailableReason = "availability probe failed: " + ex.Message;
        }

        if (!available)
            Log.Warn($"Runtime '{spec.Id}' ({spec.Language}) is not available: {unavailableReason}");

        return new RuntimeStatus(spec.Id, spec.Language, available, unavailableReason);
    }

    private static RuntimeStatus Unavailable(Spec spec, string reason)
    {
        Log.Warn($"Runtime '{spec.Id}' ({spec.Language}): {reason}");
        return new RuntimeStatus(spec.Id, spec.Language, false, reason);
    }

    private static Type? ResolveType(Spec spec, ModPaths paths, out string? problem)
    {
        problem = null;
        foreach (var name in spec.TypeNames)
        {
            try
            {
                var type = Type.GetType(name, throwOnError: false);
                if (type is not null)
                    return type;
            }
            catch (Exception ex)
            {
                // The assembly exists but could not be loaded (a missing dependency, a bad image).
                problem = $"runtime not available: {ex.Message}";
            }
        }

        // The default probe found nothing: load the assembly from the mod folder explicitly.
        try
        {
            var file = Path.Combine(paths.InstallDir, spec.AssemblyName + ".dll");
            if (!File.Exists(file))
                return null;

            var context = AssemblyLoadContext.GetLoadContext(typeof(RuntimeLoader).Assembly) ?? AssemblyLoadContext.Default;
            var assembly = context.LoadFromAssemblyPath(file);
            foreach (var name in spec.TypeNames)
            {
                var typeName = name.Split(',')[0].Trim();
                var type = assembly.GetType(typeName, throwOnError: false);
                if (type is not null)
                    return type;
            }
        }
        catch (Exception ex)
        {
            problem = $"runtime not available: {ex.Message}";
        }

        return null;
    }
}
