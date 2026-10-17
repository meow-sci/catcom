namespace Catcom.Core.Editor;

/// <summary>A request frame: <c>[0, msgid, method, params]</c>.</summary>
/// <param name="MsgId">The id the response must echo.</param>
/// <param name="Method">The method (API function) name.</param>
/// <param name="Args">The positional arguments.</param>
public sealed record RpcRequest(uint MsgId, string Method, IReadOnlyList<object?> Args) : RpcMessage;
