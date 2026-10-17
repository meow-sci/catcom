using Microsoft.ClearScript;

namespace Catcom.Runtime.V8;

/// <summary>The runtime's record behind a <see cref="Catcom.Core.Values.ScriptCallable"/>: the JS function and its owner.</summary>
/// <param name="Owner">The script the function belongs to.</param>
/// <param name="Function">The JS function object.</param>
internal sealed record CallableEntry(LoadedScript Owner, ScriptObject Function);
