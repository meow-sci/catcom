namespace Catcom.Core.Values;

/// <summary>The kind of a <see cref="ScriptValue"/>.</summary>
public enum ScriptValueKind
{
    /// <summary>The absence of a value (Lua <c>nil</c>, JS <c>null</c>/<c>undefined</c>).</summary>
    Null,

    /// <summary>A boolean.</summary>
    Bool,

    /// <summary>A double-precision number (the only numeric kind).</summary>
    Number,

    /// <summary>A text string.</summary>
    String,

    /// <summary>A byte buffer.</summary>
    Bytes,

    /// <summary>An ordered list of values.</summary>
    List,

    /// <summary>A string-keyed map of values (insertion order preserved).</summary>
    Map,

    /// <summary>An opaque, runtime-owned function (<see cref="ScriptCallable"/>).</summary>
    Function,

    /// <summary>An opaque string handle to a host object (<c>"vessel:&lt;id&gt;"</c>, <c>"time"</c>, …).</summary>
    Handle,
}
