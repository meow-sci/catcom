using Catcom.Core.Hosting;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>One recorded <see cref="IHostFacade.Log"/> call, with the script that was current when it happened.</summary>
public sealed record LogCall(int Level, string Message, string Script);
