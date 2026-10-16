namespace KsaScripting.Core.Editor;

/// <summary>One decoded msgpack-RPC frame: a <see cref="RpcRequest"/>, <see cref="RpcResponse"/> or <see cref="RpcNotification"/>.</summary>
public abstract record RpcMessage;
