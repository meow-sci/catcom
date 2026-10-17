namespace Catcom.Core.Editor;

/// <summary>A response frame: <c>[1, msgid, error, result]</c>. <paramref name="Error"/> is <c>null</c> on success.</summary>
/// <param name="MsgId">The id of the request this answers.</param>
/// <param name="Error">The error value (Neovim sends <c>[type, message]</c>), or <c>null</c>.</param>
/// <param name="Result">The result value.</param>
public sealed record RpcResponse(uint MsgId, object? Error, object? Result) : RpcMessage;
