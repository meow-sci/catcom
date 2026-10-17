using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>A temporary scripts directory plus an initialized <see cref="LuaRuntime"/> bound to a <see cref="FakeHostFacade"/>.</summary>
public sealed class LuaRuntimeFixture : IDisposable
{
    private readonly DirectoryInfo _root;

    /// <summary>Creates the fixture.</summary>
    /// <param name="jitEnabled">Whether the runtime leaves the JIT on.</param>
    /// <param name="heapLimitBytes">The soft heap limit (0 = unlimited).</param>
    public LuaRuntimeFixture(bool jitEnabled = true, long heapLimitBytes = 0)
    {
        _root = Directory.CreateTempSubdirectory("ksa-lua-tests-");
        ScriptsDir = Path.Combine(_root.FullName, "scripts");
        Directory.CreateDirectory(ScriptsDir);
        Host = new FakeHostFacade();
        Runtime = new LuaRuntime();
        Runtime.Initialize(Host, new RuntimeOptions(ScriptsDir, Path.Combine(_root.FullName, "stubs"), heapLimitBytes, jitEnabled, null));
    }

    /// <summary>The scripts directory.</summary>
    public string ScriptsDir { get; }

    /// <summary>The recording host.</summary>
    public FakeHostFacade Host { get; }

    /// <summary>The runtime under test.</summary>
    public LuaRuntime Runtime { get; }

    /// <summary>Writes <paramref name="code"/> to <c>scripts/&lt;name&gt;.lua</c> and returns the source for it.</summary>
    /// <param name="name">The script name (may contain '/').</param>
    /// <param name="code">The Lua source.</param>
    /// <returns>The script source.</returns>
    public ScriptSource Write(string name, string code)
    {
        var path = Path.Combine(ScriptsDir, name.Replace('/', Path.DirectorySeparatorChar) + ".lua");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, code);
        return new ScriptSource(name, path, code, ScriptLanguages.Lua);
    }

    /// <summary>Writes and loads a script.</summary>
    /// <param name="name">The script name.</param>
    /// <param name="code">The Lua source.</param>
    /// <returns>The loaded module.</returns>
    public ScriptModule Load(string name, string code) => Runtime.Load(Write(name, code));

    /// <summary>Loads a script and invokes one of its exports.</summary>
    /// <param name="code">The Lua source (typically <c>return { f = function(...) ... end }</c>).</param>
    /// <param name="export">The export to invoke.</param>
    /// <param name="args">The arguments.</param>
    /// <returns>The call result.</returns>
    public ScriptValue Call(string code, string export, params ScriptValue[] args)
    {
        var module = Load("t" + Guid.NewGuid().ToString("N")[..8], code);
        return Runtime.Invoke(Runtime.GetExport(module, export), args);
    }

    /// <summary>Gets an export of a module.</summary>
    /// <param name="module">The module.</param>
    /// <param name="name">The export name.</param>
    /// <returns>The callable.</returns>
    public ScriptCallable Export(ScriptModule module, string name) => Runtime.GetExport(module, name);

    /// <inheritdoc />
    public void Dispose()
    {
        Runtime.Dispose();
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup of the temp directory
        }
    }
}
