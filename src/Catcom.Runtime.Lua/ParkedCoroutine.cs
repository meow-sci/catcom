using Catcom.Core.Hosting;

namespace Catcom.Runtime.Lua;

/// <summary>A coroutine that yielded a host wait and is waiting for it to complete.</summary>
internal sealed class ParkedCoroutine
{
    /// <summary>Creates a parked coroutine.</summary>
    /// <param name="thread">The coroutine's <c>lua_State*</c>.</param>
    /// <param name="threadRef">The registry reference anchoring the coroutine.</param>
    /// <param name="wait">The wait it is parked on.</param>
    /// <param name="owner">The module that owns the running function, if any.</param>
    /// <param name="scriptName">The script name host calls are attributed to while it runs.</param>
    public ParkedCoroutine(nint thread, int threadRef, HostWait wait, ModuleState? owner, string scriptName)
    {
        Thread = thread;
        ThreadRef = threadRef;
        Wait = wait;
        Owner = owner;
        ScriptName = scriptName;
    }

    /// <summary>The coroutine's <c>lua_State*</c>.</summary>
    public nint Thread { get; }

    /// <summary>The registry reference anchoring the coroutine.</summary>
    public int ThreadRef { get; }

    /// <summary>The wait it is parked on.</summary>
    public HostWait Wait { get; }

    /// <summary>The module that owns the running function, if any.</summary>
    public ModuleState? Owner { get; }

    /// <summary>The script name host calls are attributed to while it runs.</summary>
    public string ScriptName { get; }
}
