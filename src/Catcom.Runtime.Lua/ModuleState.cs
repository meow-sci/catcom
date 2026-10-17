using Catcom.Core.Values;
using Catcom.Runtime.Lua.Native;

namespace Catcom.Runtime.Lua;

/// <summary>Everything a loaded module owns inside the Lua state (registry references and callables).</summary>
internal sealed class ModuleState
{
    /// <summary>Creates the state for a module being loaded.</summary>
    /// <param name="token">The module token.</param>
    /// <param name="name">The script name.</param>
    /// <param name="path">The source file path.</param>
    public ModuleState(long token, string name, string path)
    {
        Token = token;
        Name = name;
        Path = path;
    }

    /// <summary>The module token (carried by the public <c>ScriptModule</c>).</summary>
    public long Token { get; }

    /// <summary>The script name (what <c>IHostFacade.EnterScript</c> is given).</summary>
    public string Name { get; }

    /// <summary>The source file path.</summary>
    public string Path { get; }

    /// <summary>Registry reference of the module's environment table.</summary>
    public int EnvRef { get; set; } = LuaNative.LUA_NOREF;

    /// <summary>Export name to registry reference of the exported function.</summary>
    public Dictionary<string, int> ExportRefs { get; } = new(StringComparer.Ordinal);

    /// <summary>The callable handed out by <c>GetExport</c> per export name (re-created once disposed).</summary>
    public Dictionary<string, ScriptCallable> ExportCallables { get; } = new(StringComparer.Ordinal);

    /// <summary>Tokens of every callable created while this module was executing.</summary>
    public HashSet<long> OwnedCallables { get; } = [];
}
