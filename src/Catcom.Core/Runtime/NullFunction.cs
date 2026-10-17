using Catcom.Core.Hosting;
using Catcom.Core.Values;

namespace Catcom.Core.Runtime;

/// <summary>A script function implemented as a C# lambda (see <see cref="NullRuntime"/>).</summary>
/// <param name="host">The host facade, as a real runtime's prelude would see it.</param>
/// <param name="args">The call arguments (for hooks: the context Map).</param>
/// <returns>The result, or a marker from <see cref="NullRuntime.Await"/> to suspend.</returns>
public delegate ScriptValue NullFunction(IHostFacade host, ScriptValue[] args);
