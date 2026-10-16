using KsaScripting.Core.Hosting;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.Lua.Tests;

/// <summary>One recorded <see cref="IHostFacade.Wait"/> call.</summary>
public sealed record WaitCall(string Kind, ScriptValue Arg, HostWait Wait);
