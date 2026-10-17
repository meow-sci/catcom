using System.Buffers;
using System.Collections;
using MessagePack;

namespace Catcom.Core.Editor;

/// <summary>
///     The msgpack-RPC frame codec used to talk to Neovim: <c>[0, msgid, method, params]</c> requests,
///     <c>[1, msgid, error, result]</c> responses and <c>[2, method, params]</c> notifications, built on
///     MessagePack-CSharp's low-level reader and writer (no resolvers or attributes).
///     <para>
///         Value mapping, both directions: <c>null</c>, <see cref="bool"/>, integers (decoded as <see cref="long"/>,
///         or <see cref="ulong"/> beyond <see cref="long.MaxValue"/>), floats (decoded as <see cref="double"/>),
///         <see cref="string"/> (UTF-8 str), <c>byte[]</c> (bin), arrays (decoded as <c>object?[]</c>), maps (decoded as
///         <c>Dictionary&lt;object, object?&gt;</c>, string keys stay strings) and Neovim's Buffer/Window/Tabpage EXT
///         types (codes 0/1/2, decoded as <see cref="NvimHandle"/>). Encoding also accepts any
///         <see cref="IReadOnlyDictionary{TKey,TValue}"/> with string keys, non-generic <see cref="IDictionary"/> and
///         <see cref="IEnumerable"/>.
///     </para>
/// </summary>
public static class MsgPackRpc
{
    private const int MaxDepth = 128;
    private const int RequestType = 0;
    private const int ResponseType = 1;
    private const int NotificationType = 2;

    /// <summary>Encodes a request frame.</summary>
    /// <param name="msgId">The message id the response will echo.</param>
    /// <param name="method">The method name.</param>
    /// <param name="args">The positional arguments.</param>
    /// <returns>The frame bytes.</returns>
    /// <exception cref="ArgumentException">An argument has a type with no msgpack mapping.</exception>
    public static byte[] EncodeRequest(uint msgId, string method, IReadOnlyList<object?> args)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(args);
        var buffer = new ArrayBufferWriter<byte>(128);
        var w = new MessagePackWriter(buffer);
        w.WriteArrayHeader(4);
        w.Write(RequestType);
        w.Write(msgId);
        w.Write(method);
        WriteList(ref w, args, 1);
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes a notification frame.</summary>
    /// <param name="method">The event name.</param>
    /// <param name="args">The positional arguments.</param>
    /// <returns>The frame bytes.</returns>
    /// <exception cref="ArgumentException">An argument has a type with no msgpack mapping.</exception>
    public static byte[] EncodeNotification(string method, IReadOnlyList<object?> args)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(args);
        var buffer = new ArrayBufferWriter<byte>(128);
        var w = new MessagePackWriter(buffer);
        w.WriteArrayHeader(3);
        w.Write(NotificationType);
        w.Write(method);
        WriteList(ref w, args, 1);
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Encodes a response frame.</summary>
    /// <param name="msgId">The id of the request being answered.</param>
    /// <param name="error">The error value (<c>null</c> for success; Neovim uses <c>[type, message]</c>).</param>
    /// <param name="result">The result value.</param>
    /// <returns>The frame bytes.</returns>
    /// <exception cref="ArgumentException">A value has a type with no msgpack mapping.</exception>
    public static byte[] EncodeResponse(uint msgId, object? error, object? result)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        var w = new MessagePackWriter(buffer);
        w.WriteArrayHeader(4);
        w.Write(ResponseType);
        w.Write(msgId);
        WriteValue(ref w, error, 1);
        WriteValue(ref w, result, 1);
        w.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    ///     Decodes the next complete frame from the front of <paramref name="buffer"/> and advances it past
    ///     that frame. Returns <c>false</c> (and leaves the buffer untouched) when more bytes are needed.
    /// </summary>
    /// <param name="buffer">The received bytes; sliced past the frame on success.</param>
    /// <param name="message">The decoded frame, or <c>null</c> when <c>false</c> is returned.</param>
    /// <returns><c>true</c> when a frame was decoded.</returns>
    /// <exception cref="FormatException">The next frame is complete but malformed (the buffer is left untouched).</exception>
    public static bool TryDecode(ref ReadOnlySequence<byte> buffer, out RpcMessage? message)
    {
        message = null;
        if (buffer.IsEmpty)
            return false;

        // Find the end of the first MessagePack value without materialising it, so a partial
        // frame costs no allocations and no exceptions.
        if (!TryFindFrameEnd(buffer, out var end))
            return false;

        message = DecodeFrame(buffer.Slice(0, end));
        buffer = buffer.Slice(end);
        return true;
    }

    // Iteratively walks one MessagePack value using only the format's length prefixes. Returns
    // false when the buffer ends inside the value. (MessagePackReader.TrySkip is internal.)
    private static bool TryFindFrameEnd(in ReadOnlySequence<byte> buffer, out SequencePosition end)
    {
        end = default;
        var reader = new SequenceReader<byte>(buffer);
        long pending = 1; // values still to skip
        while (pending > 0)
        {
            if (!reader.TryRead(out var code))
                return false;

            pending--;
            long skip;
            switch (code)
            {
                case <= 0x7f or >= 0xe0 or 0xc0 or 0xc2 or 0xc3: // fixint, nil, false, true
                    continue;
                case >= 0xa0 and <= 0xbf: // fixstr
                    skip = code & 0x1f;
                    break;
                case >= 0x90 and <= 0x9f: // fixarray
                    pending += code & 0x0f;
                    continue;
                case >= 0x80 and <= 0x8f: // fixmap
                    pending += 2L * (code & 0x0f);
                    continue;
                case 0xc4 or 0xd9: // bin8, str8
                    if (!reader.TryRead(out var len8))
                        return false;
                    skip = len8;
                    break;
                case 0xc5 or 0xda: // bin16, str16
                    if (!reader.TryReadBigEndian(out short len16))
                        return false;
                    skip = (ushort)len16;
                    break;
                case 0xc6 or 0xdb: // bin32, str32
                    if (!reader.TryReadBigEndian(out int len32))
                        return false;
                    skip = (uint)len32;
                    break;
                case 0xc7: // ext8: length, type, data
                    if (!reader.TryRead(out var extLen8))
                        return false;
                    skip = 1L + extLen8;
                    break;
                case 0xc8: // ext16
                    if (!reader.TryReadBigEndian(out short extLen16))
                        return false;
                    skip = 1L + (ushort)extLen16;
                    break;
                case 0xc9: // ext32
                    if (!reader.TryReadBigEndian(out int extLen32))
                        return false;
                    skip = 1L + (uint)extLen32;
                    break;
                case 0xca or 0xce or 0xd2: // float32, uint32, int32
                    skip = 4;
                    break;
                case 0xcb or 0xcf or 0xd3: // float64, uint64, int64
                    skip = 8;
                    break;
                case 0xcc or 0xd0: // uint8, int8
                    skip = 1;
                    break;
                case 0xcd or 0xd1: // uint16, int16
                    skip = 2;
                    break;
                case 0xd4: // fixext1: type + 1 byte
                    skip = 2;
                    break;
                case 0xd5:
                    skip = 3;
                    break;
                case 0xd6:
                    skip = 5;
                    break;
                case 0xd7:
                    skip = 9;
                    break;
                case 0xd8:
                    skip = 17;
                    break;
                case 0xdc: // array16
                    if (!reader.TryReadBigEndian(out short arr16))
                        return false;
                    pending += (ushort)arr16;
                    continue;
                case 0xdd: // array32
                    if (!reader.TryReadBigEndian(out int arr32))
                        return false;
                    pending += (uint)arr32;
                    continue;
                case 0xde: // map16
                    if (!reader.TryReadBigEndian(out short map16))
                        return false;
                    pending += 2L * (ushort)map16;
                    continue;
                case 0xdf: // map32
                    if (!reader.TryReadBigEndian(out int map32))
                        return false;
                    pending += 2L * (uint)map32;
                    continue;
                default: // 0xc1 is never used
                    throw new FormatException($"invalid msgpack code 0x{code:x2}");
            }

            if (reader.Remaining < skip)
                return false;
            reader.Advance(skip);
        }

        end = reader.Position;
        return true;
    }

    private static RpcMessage DecodeFrame(ReadOnlySequence<byte> frame)
    {
        try
        {
            var r = new MessagePackReader(frame);
            var count = r.ReadArrayHeader();
            var type = r.ReadInt32();
            RpcMessage message;
            switch (type)
            {
                case RequestType when count == 4:
                {
                    var id = r.ReadUInt32();
                    var method = r.ReadString() ?? throw new FormatException("request method is nil");
                    message = new RpcRequest(id, method, ReadArgs(ref r));
                    break;
                }

                case ResponseType when count == 4:
                {
                    var id = r.ReadUInt32();
                    var error = ReadValue(ref r, 1);
                    var result = ReadValue(ref r, 1);
                    message = new RpcResponse(id, error, result);
                    break;
                }

                case NotificationType when count == 3:
                {
                    var method = r.ReadString() ?? throw new FormatException("notification method is nil");
                    message = new RpcNotification(method, ReadArgs(ref r));
                    break;
                }

                default:
                    throw new FormatException($"unexpected msgpack-rpc frame (type {type}, {count} elements)");
            }

            return message;
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException("malformed msgpack-rpc frame: " + ex.Message, ex);
        }
    }

    private static object?[] ReadArgs(ref MessagePackReader r)
    {
        if (r.IsNil)
        {
            r.ReadNil();
            return [];
        }

        return ReadValue(ref r, 1) as object?[] ?? throw new FormatException("params is not an array");
    }

    private static void WriteList(ref MessagePackWriter w, IReadOnlyList<object?> items, int depth)
    {
        w.WriteArrayHeader(items.Count);
        for (var i = 0; i < items.Count; i++)
            WriteValue(ref w, items[i], depth + 1);
    }

    private static void WriteValue(ref MessagePackWriter w, object? value, int depth)
    {
        if (depth > MaxDepth)
            throw new ArgumentException("value is nested too deeply to encode");

        switch (value)
        {
            case null:
                w.WriteNil();
                break;
            case bool b:
                w.Write(b);
                break;
            case long l:
                w.Write(l);
                break;
            case int i:
                w.Write(i);
                break;
            case short s:
                w.Write(s);
                break;
            case sbyte sb:
                w.Write(sb);
                break;
            case ulong ul:
                w.Write(ul);
                break;
            case uint ui:
                w.Write(ui);
                break;
            case ushort us:
                w.Write(us);
                break;
            case byte by:
                w.Write(by);
                break;
            case double d:
                w.Write(d);
                break;
            case float f:
                w.Write(f);
                break;
            case string str:
                w.Write(str);
                break;
            case byte[] bytes:
                w.Write(new ReadOnlySpan<byte>(bytes));
                break;
            case ReadOnlyMemory<byte> memory:
                w.Write(memory.Span);
                break;
            case NvimHandle handle:
                WriteHandle(ref w, handle);
                break;
            case IReadOnlyDictionary<string, object?> map:
                w.WriteMapHeader(map.Count);
                foreach (var pair in map)
                {
                    w.Write(pair.Key);
                    WriteValue(ref w, pair.Value, depth + 1);
                }

                break;
            case IDictionary dictionary:
                w.WriteMapHeader(dictionary.Count);
                foreach (DictionaryEntry entry in dictionary)
                {
                    WriteValue(ref w, entry.Key, depth + 1);
                    WriteValue(ref w, entry.Value, depth + 1);
                }

                break;
            case IReadOnlyList<object?> list:
                WriteList(ref w, list, depth);
                break;
            case IEnumerable sequence:
            {
                var items = new List<object?>();
                foreach (var item in sequence)
                    items.Add(item);
                WriteList(ref w, items, depth);
                break;
            }

            default:
                throw new ArgumentException($"no msgpack mapping for type {value.GetType()}");
        }
    }

    private static void WriteHandle(ref MessagePackWriter w, NvimHandle handle)
    {
        if (handle.Kind is < 0 or > 2)
            throw new ArgumentException($"unknown Neovim handle kind {handle.Kind}");

        var payload = new ArrayBufferWriter<byte>(9);
        var pw = new MessagePackWriter(payload);
        pw.Write(handle.Value);
        pw.Flush();
        w.WriteExtensionFormatHeader(new ExtensionHeader((sbyte)handle.Kind, (uint)payload.WrittenCount));
        w.WriteRaw(payload.WrittenSpan);
    }

    private static object? ReadValue(ref MessagePackReader r, int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException("value is nested too deeply to decode");

        switch (r.NextMessagePackType)
        {
            case MessagePackType.Nil:
                r.ReadNil();
                return null;
            case MessagePackType.Boolean:
                return r.ReadBoolean();
            case MessagePackType.Integer:
                if (r.NextCode == MessagePackCode.UInt64)
                {
                    var unsigned = r.ReadUInt64();
                    if (unsigned <= long.MaxValue)
                        return (long)unsigned;
                    return unsigned;
                }

                return r.ReadInt64();
            case MessagePackType.Float:
                return r.ReadDouble();
            case MessagePackType.String:
                return r.ReadString();
            case MessagePackType.Binary:
                return r.ReadBytes()?.ToArray() ?? [];
            case MessagePackType.Array:
            {
                var count = r.ReadArrayHeader();
                if (count > r.Sequence.Length - r.Consumed)
                    throw new FormatException("array header claims more elements than bytes remain");
                var items = new object?[count];
                for (var i = 0; i < items.Length; i++)
                    items[i] = ReadValue(ref r, depth + 1);
                return items;
            }

            case MessagePackType.Map:
            {
                var count = r.ReadMapHeader();
                if (count > r.Sequence.Length - r.Consumed)
                    throw new FormatException("map header claims more entries than bytes remain");
                var map = new Dictionary<object, object?>(count);
                for (var i = 0; i < count; i++)
                {
                    var key = ReadValue(ref r, depth + 1) ?? throw new FormatException("map key is nil");
                    map[key] = ReadValue(ref r, depth + 1);
                }

                return map;
            }

            case MessagePackType.Extension:
            {
                var ext = r.ReadExtensionFormat();
                if (ext.TypeCode is < 0 or > 2)
                    throw new FormatException($"unknown msgpack extension type {ext.TypeCode}");
                var payload = new MessagePackReader(ext.Data);
                return new NvimHandle(ext.TypeCode, payload.ReadInt64());
            }

            default:
                throw new FormatException("unsupported msgpack type " + r.NextMessagePackType);
        }
    }
}
