namespace KsaScripting.Core.Api;

/// <summary>
///     The single definition of the script-facing API. The TypeScript declarations
///     (<c>stubs/host.d.ts</c>) and the LuaCATS stubs (<c>stubs/ksa.lua</c>) are generated from it,
///     and the hand-written preludes (<c>Preludes/ksa.js</c>, <c>Preludes/ksa.lua</c>) are checked
///     against it by tests. The data classes (<c>EngineInfo</c>, <c>OrbitInfo</c>, ...) are reflected
///     from the snapshot records, and the <c>Events</c> class is built from the event catalog, so
///     neither can drift from the host.
/// </summary>
public static class ApiSchema
{
    private static readonly Lazy<IReadOnlyList<ApiClass>> All = new(Build);

    /// <summary>
    ///     Every class, in documentation order: the <c>ksa</c> module, the clock, vessels and module
    ///     refs, the reflected data classes, the value and service types, the contexts and the
    ///     <c>Events</c> catalog.
    /// </summary>
    public static IReadOnlyList<ApiClass> Classes => All.Value;

    /// <summary>Looks a class up by name.</summary>
    /// <param name="name">The class name, e.g. <c>"VesselRef"</c>.</param>
    /// <returns>The class.</returns>
    /// <exception cref="ArgumentException">There is no class with that name.</exception>
    public static ApiClass Get(string name)
        => Classes.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
           ?? throw new ArgumentException($"Unknown API class '{name}'.", nameof(name));

    private static IReadOnlyList<ApiClass> Build()
    {
        var core = CoreClasses.Create();
        var classes = new List<ApiClass>();
        classes.AddRange(core);
        classes.AddRange(ReflectedClasses.Create());
        classes.AddRange(ValueClasses.Create());
        classes.AddRange(ContextClasses.Create());
        classes.AddRange(EventClasses.Create());
        return classes;
    }
}
