namespace Catcom.Core.Editor;

/// <summary>A notification frame: <c>[2, method, params]</c> (no response expected).</summary>
/// <param name="Method">The event name.</param>
/// <param name="Args">The positional arguments.</param>
public sealed record RpcNotification(string Method, IReadOnlyList<object?> Args) : RpcMessage;
