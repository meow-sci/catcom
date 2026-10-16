using KsaScripting.Core.Hosting;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.Lua.Tests;

/// <summary>One recorded <see cref="IHostFacade.Subscribe"/> call.</summary>
public sealed record SubscriptionCall(Guid Id, string Kind, string Key, ScriptCallable Fn, BindingOptions Options);
