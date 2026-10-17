namespace Catcom.Core.Api;

/// <summary>Type constructors used by the schema definitions (internal builder vocabulary).</summary>
internal static class Ty
{
    internal static readonly ApiType Number = new("number", "number");
    internal static readonly ApiType Boolean = new("boolean", "boolean");
    internal static readonly ApiType String = new("string", "string");
    internal static readonly ApiType Void = new("void", "nil");
    internal static readonly ApiType Unknown = new("unknown", "any");
    internal static readonly ApiType UnknownMap = new("Record<string, unknown>", "table<string, any>");
    internal static readonly ApiType Clock = new("\"ut\" | \"wall\" | \"render\"", "\"ut\"|\"wall\"|\"render\"");
    internal static readonly ApiType EventName = new("EventName", "ksa.EventName|string");

    /// <summary>A reference to a schema class (<c>VesselRef</c> / <c>ksa.VesselRef</c>).</summary>
    internal static ApiType Ref(string className) => new(className, "ksa." + className);

    /// <summary>An array of <paramref name="element"/>.</summary>
    internal static ApiType Array(ApiType element)
        => new(WrapUnion(element.TsType) + "[]", WrapUnion(element.LuaType) + "[]");

    /// <summary><paramref name="inner"/> or <c>null</c> (TypeScript <c>| null</c>, Lua optional).</summary>
    internal static ApiType Nullable(ApiType inner) => new(inner.TsType + " | null", inner.LuaType + "?");

    /// <summary><paramref name="inner"/> or <c>undefined</c> (TypeScript <c>| undefined</c>, Lua optional).</summary>
    internal static ApiType Optional(ApiType inner) => new(inner.TsType + " | undefined", inner.LuaType + "?");

    /// <summary>A function type (<c>(a: A) =&gt; R</c> / <c>fun(a: A): R</c>).</summary>
    internal static ApiType Func(string tsParams, string tsReturn, string luaParams, string? luaReturn = null)
        => new($"({tsParams}) => {tsReturn}", luaReturn is null ? $"fun({luaParams})" : $"fun({luaParams}): {luaReturn}");

    private static string WrapUnion(string type)
        => type.Contains(' ', StringComparison.Ordinal) || type.Contains('|', StringComparison.Ordinal) ? "(" + type + ")" : type;
}
