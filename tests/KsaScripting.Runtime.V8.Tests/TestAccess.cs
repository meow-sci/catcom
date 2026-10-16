namespace KsaScripting.Runtime.V8.Tests;

/// <summary>Test-only access to the runtime's internals.</summary>
internal static class TestAccess
{
    /// <summary>The runtime's TypeScript compiler.</summary>
    public static TypeScriptCompiler CompilerForTests(this V8ScriptRuntime runtime) => runtime.Compiler
        ?? throw new InvalidOperationException("the runtime has no compiler (not initialised or unavailable)");
}
