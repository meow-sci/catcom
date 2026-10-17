using Catcom.Core.Hosting;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>One recorded <see cref="IHostFacade.Wait"/> call.</summary>
public sealed record WaitCall(string Kind, ScriptValue Arg, HostWait Wait);
