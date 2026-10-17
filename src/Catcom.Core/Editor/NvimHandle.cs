namespace Catcom.Core.Editor;

/// <summary>
///     A Neovim object handle (msgpack EXT type): <see cref="Kind"/> 0 = Buffer, 1 = Window, 2 = Tabpage;
///     <see cref="Value"/> is the object's id. Encoded and decoded as the same EXT by <see cref="MsgPackRpc"/>.
/// </summary>
/// <param name="Kind">The EXT type code (0 buffer, 1 window, 2 tabpage).</param>
/// <param name="Value">The handle's integer id.</param>
public sealed record NvimHandle(int Kind, long Value);
