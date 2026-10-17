using Catcom.Core.Hosting;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>One recorded <see cref="IHostFacade.Subscribe"/> call.</summary>
public sealed record SubscriptionCall(Guid Id, string Kind, string Key, ScriptCallable Fn, BindingOptions Options);
