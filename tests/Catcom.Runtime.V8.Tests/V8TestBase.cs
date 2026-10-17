using System.Diagnostics;
using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.V8.Tests;

/// <summary>Shared fixture: a temp scripts folder (with <c>stubs/</c>), a fake host and an initialised V8 runtime per test.</summary>
public abstract class V8TestBase
{
    private static readonly string SharedStubsDir = CreateSharedStubsDir();
    private readonly List<IDisposable> _disposables = [];

    /// <summary>The per-test temp root.</summary>
    protected string Root { get; private set; } = "";

    /// <summary>The scripts folder.</summary>
    protected string ScriptsDir { get; private set; } = "";

    /// <summary>The stubs folder (shared by all tests so the compiler code cache is reused).</summary>
    protected string StubsDir => SharedStubsDir;

    /// <summary>The stubs folder shared by all tests, for fixtures that are not derived from this class.</summary>
    internal static string SharedStubsFolder => SharedStubsDir;

    /// <summary>The fake host facade.</summary>
    protected FakeHostFacade Host { get; private set; } = null!;

    /// <summary>The runtime under test.</summary>
    protected V8ScriptRuntime Rt { get; private set; } = null!;

    /// <summary>How long a test waits for asynchronous work before failing.</summary>
    protected static TimeSpan Deadline => TimeSpan.FromSeconds(10);

    [SetUp]
    public void BaseSetUp()
    {
        Root = Directory.CreateTempSubdirectory("ksa-v8-test-").FullName;
        ScriptsDir = Path.Combine(Root, "scripts");
        Directory.CreateDirectory(ScriptsDir);
        Host = new FakeHostFacade();
        Rt = NewRuntime();
    }

    [TearDown]
    public void BaseTearDown()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
        _disposables.Clear();
        Rt.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind is not a test failure.
        }
    }

    /// <summary>Creates and initialises a runtime over this test's scripts folder.</summary>
    protected V8ScriptRuntime NewRuntime(long heapLimitBytes = 0, int? debugPort = null, string? stubsDir = null)
    {
        var runtime = new V8ScriptRuntime();
        runtime.Initialize(Host, new RuntimeOptions(ScriptsDir, stubsDir ?? StubsDir, heapLimitBytes, true, debugPort));
        if (!ReferenceEquals(runtime, Rt) && Rt is not null)
            _disposables.Add(runtime);
        return runtime;
    }

    /// <summary>Writes a file under the scripts folder and returns its absolute path.</summary>
    protected string WriteScript(string relativePath, string code)
    {
        var path = Path.Combine(ScriptsDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, code);
        return path;
    }

    /// <summary>Writes <c>scripts/&lt;name&gt;.ts</c> and loads it as the entry module.</summary>
    protected ScriptModule LoadScript(string name, string code, V8ScriptRuntime? runtime = null)
    {
        var path = WriteScript(name + ".ts", code);
        return (runtime ?? Rt).Load(new ScriptSource(name, path, code, ScriptLanguages.TypeScript));
    }

    /// <summary>Loads a script, fetches an export and invokes it with the given arguments.</summary>
    protected ScriptValue Call(string scriptName, string code, string export, params ScriptValue[] args)
    {
        var module = LoadScript(scriptName, code);
        return Rt.Invoke(Rt.GetExport(module, export), args);
    }

    /// <summary>Advances the runtime until <paramref name="condition"/> holds, waiting on the work signal (never a fixed sleep).</summary>
    protected void PumpUntil(Func<bool> condition, string what, V8ScriptRuntime? runtime = null)
    {
        var rt = runtime ?? Rt;
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Deadline)
                Assert.Fail($"timed out waiting for {what}");

            rt.WaitForWork(TimeSpan.FromMilliseconds(20));
            rt.Advance(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Removes the shared stubs folder (called once after the last test).</summary>
    internal static void DeleteSharedStubsDir()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(SharedStubsDir)!, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static string CreateSharedStubsDir()
    {
        var root = Directory.CreateTempSubdirectory("ksa-v8-shared-").FullName;
        var stubs = Path.Combine(root, "stubs");
        Directory.CreateDirectory(stubs);
        return stubs;
    }
}
