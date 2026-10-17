using System.Buffers;
using Catcom.Core.Editor;

namespace Catcom.Core.Tests.Editor;

/// <summary>The msgpack-RPC frame codec: value round trips, exact bytes, partial/multiple frames, malformed input.</summary>
[TestFixture]
public sealed class MsgPackRpcTests
{
    private static Dictionary<object, object?> Map(params (object Key, object? Value)[] entries)
    {
        var map = new Dictionary<object, object?>();
        foreach (var (key, value) in entries)
            map[key] = value;
        return map;
    }

    private static object?[] Arr(params object?[] items) => items;

    private static TestCaseData Case(string label, object? input, object? expected)
        => new TestCaseData(input, expected).SetName("RoundTrip_" + label);

    public static IEnumerable<TestCaseData> ValueCases()
    {
        yield return Case("nil", null, null);
        yield return Case("true", true, true);
        yield return Case("false", false, false);

        foreach (var n in new long[]
                 {
                     0, 1, 127, 128, 255, 256, 65535, 65536, int.MaxValue, uint.MaxValue, (long)uint.MaxValue + 1, long.MaxValue,
                     -1, -32, -33, -128, -129, -32768, -32769, int.MinValue, (long)int.MinValue - 1, long.MinValue,
                 })
        {
            yield return Case("long_" + n, n, n);
        }

        yield return Case("int_neg", -300, -300L);
        yield return Case("uint_max", uint.MaxValue, (long)uint.MaxValue);
        yield return Case("short", (short)-2, -2L);
        yield return Case("ushort", ushort.MaxValue, (long)ushort.MaxValue);
        yield return Case("byte", (byte)200, 200L);
        yield return Case("sbyte", (sbyte)-100, -100L);
        yield return Case("ulong_small_decodes_as_long", 5UL, 5L);
        yield return Case("ulong_above_long_max", (ulong)long.MaxValue + 1, (ulong)long.MaxValue + 1);
        yield return Case("ulong_max", ulong.MaxValue, ulong.MaxValue);

        yield return Case("double", -2.25, -2.25);
        yield return Case("double_max", double.MaxValue, double.MaxValue);
        yield return Case("double_nan", double.NaN, double.NaN);
        yield return Case("float_widens_to_double", 1.5f, 1.5);

        yield return Case("string_empty", "", "");
        yield return Case("string_ascii", "hello", "hello");
        yield return Case("string_unicode", "héllo ✓ 日本 🚀", "héllo ✓ 日本 🚀");
        yield return Case("string_31_fixstr", new string('a', 31), new string('a', 31));
        yield return Case("string_32_str8", new string('b', 32), new string('b', 32));
        yield return Case("string_300_str16", new string('c', 300), new string('c', 300));
        yield return Case("string_70000_str32", new string('d', 70000), new string('d', 70000));

        yield return Case("bin_empty", Array.Empty<byte>(), Array.Empty<byte>());
        yield return Case("bin_small", new byte[] { 0, 1, 2, 255 }, new byte[] { 0, 1, 2, 255 });
        yield return Case("bin_300", Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(), Enumerable.Range(0, 300).Select(i => (byte)i).ToArray());
        yield return Case("bin_70000", new byte[70000], new byte[70000]);
        yield return Case("bin_from_memory", new ReadOnlyMemory<byte>([9, 8, 7]), new byte[] { 9, 8, 7 });

        yield return Case("array_empty", Arr(), Arr());
        yield return Case("array_nested", Arr(1, Arr(2, Arr(3, "x")), Arr()), Arr(1L, Arr(2L, Arr(3L, "x")), Arr()));
        yield return Case("array_mixed", Arr(null, true, 1.5, "s", new byte[] { 1 }), Arr(null, true, 1.5, "s", new byte[] { 1 }));
        yield return Case("array_16_array16", Enumerable.Range(0, 16).Cast<object?>().ToArray(), Enumerable.Range(0, 16).Select(i => (object?)(long)i).ToArray());
        yield return Case("array_70000_array32", new object?[70000], new object?[70000]);
        yield return Case("list_of_strings", new List<string> { "a", "b" }, Arr("a", "b"));
        yield return Case("int_array", new[] { 1, 2, 3 }, Arr(1L, 2L, 3L));
        yield return Case("enumerable", Enumerable.Range(1, 3).Select(i => i * 2), Arr(2L, 4L, 6L));

        yield return Case("map_empty", new Dictionary<string, object?>(), Map());
        yield return Case(
            "map_nested",
            new Dictionary<string, object?>
            {
                ["a"] = 1,
                ["b"] = Arr(true, null),
                ["c"] = new Dictionary<string, object?> { ["d"] = new Dictionary<string, object?> { ["e"] = "deep" } },
            },
            Map(("a", 1L), ("b", Arr(true, null)), ("c", Map(("d", Map(("e", "deep")))))));
        yield return Case(
            "map_16_map16",
            Enumerable.Range(0, 16).ToDictionary(i => "k" + i, i => (object?)i),
            Map(Enumerable.Range(0, 16).Select(i => ((object)("k" + i), (object?)(long)i)).ToArray()));
        yield return Case("map_int_keys", new Dictionary<int, string> { [1] = "x", [2] = "y" }, Map((1L, "x"), (2L, "y")));
        yield return Case("map_read_only", new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(new Dictionary<string, object?> { ["k"] = 7 }), Map(("k", 7L)));
        yield return Case("map_generic_value_type", new Dictionary<string, int> { ["n"] = 3 }, Map(("n", 3L)));

        yield return Case("handle_buffer_0", new NvimHandle(0, 0), new NvimHandle(0, 0));
        yield return Case("handle_window_5", new NvimHandle(1, 5), new NvimHandle(1, 5));
        yield return Case("handle_tabpage_127", new NvimHandle(2, 127), new NvimHandle(2, 127));
        yield return Case("handle_128", new NvimHandle(0, 128), new NvimHandle(0, 128));
        yield return Case("handle_70000", new NvimHandle(1, 70000), new NvimHandle(1, 70000));
        yield return Case("handle_huge", new NvimHandle(2, 1L << 40), new NvimHandle(2, 1L << 40));
        yield return Case("handle_in_structure", Arr(new NvimHandle(0, 3), Map(("w", new NvimHandle(1, 9)))), Arr(new NvimHandle(0, 3), Map(("w", new NvimHandle(1, 9)))));
    }

    private static void AssertSame(object? expected, object? actual, string path = "value")
    {
        switch (expected)
        {
            case null:
                Assert.That(actual, Is.Null, path);
                return;
            case object?[] expectedArray:
                Assert.That(actual, Is.InstanceOf<object?[]>(), path);
                var actualArray = (object?[])actual!;
                Assert.That(actualArray, Has.Length.EqualTo(expectedArray.Length), path + ".Length");
                for (var i = 0; i < expectedArray.Length; i++)
                    AssertSame(expectedArray[i], actualArray[i], $"{path}[{i}]");
                return;
            case Dictionary<object, object?> expectedMap:
                Assert.That(actual, Is.InstanceOf<Dictionary<object, object?>>(), path);
                var actualMap = (Dictionary<object, object?>)actual!;
                Assert.That(actualMap, Has.Count.EqualTo(expectedMap.Count), path + ".Count");
                foreach (var (key, value) in expectedMap)
                {
                    Assert.That(actualMap.ContainsKey(key), Is.True, $"{path}[{key}] missing");
                    AssertSame(value, actualMap[key], $"{path}[{key}]");
                }

                return;
            case byte[] expectedBytes:
                Assert.That(actual, Is.InstanceOf<byte[]>(), path);
                Assert.That((byte[])actual!, Is.EqualTo(expectedBytes), path);
                return;
            case double expectedDouble when double.IsNaN(expectedDouble):
                Assert.That(actual, Is.InstanceOf<double>(), path);
                Assert.That(double.IsNaN((double)actual!), Is.True, path);
                return;
            default:
                Assert.That(actual, Is.Not.Null, path);
                Assert.That(actual!.GetType(), Is.EqualTo(expected.GetType()), path + " type");
                Assert.That(actual, Is.EqualTo(expected), path);
                return;
        }
    }

    private static RpcMessage DecodeOne(byte[] bytes)
    {
        var sequence = new ReadOnlySequence<byte>(bytes);
        Assert.That(MsgPackRpc.TryDecode(ref sequence, out var message), Is.True);
        Assert.That(sequence.IsEmpty, Is.True, "the whole frame is consumed");
        return message!;
    }

    [TestCaseSource(nameof(ValueCases))]
    public void Request_RoundTripsEveryValueKind(object? input, object? expected)
    {
        var frame = MsgPackRpc.EncodeRequest(7, "m", [input, "tail"]);
        var request = DecodeOne(frame) as RpcRequest;

        Assert.That(request, Is.Not.Null);
        Assert.That(request!.MsgId, Is.EqualTo(7u));
        Assert.That(request.Method, Is.EqualTo("m"));
        Assert.That(request.Args, Has.Count.EqualTo(2));
        AssertSame(expected, request.Args[0]);
        AssertSame("tail", request.Args[1]);
    }

    [TestCaseSource(nameof(ValueCases))]
    public void Response_RoundTripsEveryValueKindAsResultAndError(object? input, object? expected)
    {
        var frame = MsgPackRpc.EncodeResponse(uint.MaxValue, input, input);
        var response = DecodeOne(frame) as RpcResponse;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.MsgId, Is.EqualTo(uint.MaxValue));
        AssertSame(expected, response.Error, "error");
        AssertSame(expected, response.Result, "result");
    }

    [Test]
    public void Notification_RoundTrips()
    {
        var frame = MsgPackRpc.EncodeNotification("ksa_saved", ["/tmp/a b/c.ts", 3]);
        var notification = DecodeOne(frame) as RpcNotification;

        Assert.That(notification, Is.Not.Null);
        Assert.That(notification!.Method, Is.EqualTo("ksa_saved"));
        AssertSame(Arr("/tmp/a b/c.ts", 3L), notification.Args);
    }

    [Test]
    public void SuccessAndErrorResponses_RoundTrip()
    {
        var ok = (RpcResponse)DecodeOne(MsgPackRpc.EncodeResponse(1, null, Arr(4, Map(("version", "x")))));
        var failed = (RpcResponse)DecodeOne(MsgPackRpc.EncodeResponse(2, Arr(0, "boom"), null));

        Assert.Multiple(() =>
        {
            Assert.That(ok.Error, Is.Null);
            AssertSame(Arr(4L, Map(("version", "x"))), ok.Result);
            AssertSame(Arr(0L, "boom"), failed.Error);
            Assert.That(failed.Result, Is.Null);
        });
    }

    [Test]
    public void EncodeRequest_ProducesTheExactMsgpackBytes()
    {
        // [0, 1, "nvim_eval", ["1+1"]]
        var expected = new byte[] { 0x94, 0x00, 0x01, 0xa9 }
            .Concat("nvim_eval"u8.ToArray())
            .Concat(new byte[] { 0x91, 0xa3, (byte)'1', (byte)'+', (byte)'1' })
            .ToArray();

        Assert.That(MsgPackRpc.EncodeRequest(1, "nvim_eval", ["1+1"]), Is.EqualTo(expected));
    }

    [Test]
    public void EncodeNotificationAndResponse_ProduceTheExactMsgpackBytes()
    {
        Assert.Multiple(() =>
        {
            // [2, "n", []]
            Assert.That(MsgPackRpc.EncodeNotification("n", []), Is.EqualTo(new byte[] { 0x93, 0x02, 0xa1, (byte)'n', 0x90 }));
            // [1, 300, nil, true]
            Assert.That(MsgPackRpc.EncodeResponse(300, null, true), Is.EqualTo(new byte[] { 0x94, 0x01, 0xcd, 0x01, 0x2c, 0xc0, 0xc3 }));
        });
    }

    [Test]
    public void Handles_UseNeovimExtTypeEncoding()
    {
        // [0, 0, "m", [ext(1) 0x05]] — fixext1 type 1 holding the msgpack integer 5
        var frame = MsgPackRpc.EncodeRequest(0, "m", [new NvimHandle(1, 5)]);

        Assert.That(frame, Is.EqualTo(new byte[] { 0x94, 0x00, 0x00, 0xa1, (byte)'m', 0x91, 0xd4, 0x01, 0x05 }));
    }

    [Test]
    public void Decode_AcceptsNilParamsAsEmptyArgs()
    {
        var request = (RpcRequest)DecodeOne([0x94, 0x00, 0x01, 0xa1, (byte)'m', 0xc0]);

        Assert.That(request.Args, Is.Empty);
    }

    [Test]
    public void Decode_ReadsNeovimWireFormsByHand()
    {
        // Wire forms other encoders may pick: an ext8 handle wrapping a uint16, uint8, float32, str8, int64.
        byte[] valid =
        [
            0x94, 0x00, 0x05, 0xa1, (byte)'m',
            0x95,
            0xc7, 0x03, 0x00, 0xcd, 0x01, 0x00,
            0xcc, 0x80,
            0xca, 0x3f, 0xc0, 0x00, 0x00,
            0xd9, 0x02, (byte)'h', (byte)'i',
            0xd3, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xfe,
        ];
        var request = (RpcRequest)DecodeOne(valid);

        AssertSame(Arr(new NvimHandle(0, 256), 128L, 1.5, "hi", -2L), request.Args.ToArray());
    }

    private static byte[] ComplexFrame()
    {
        var args = new object?[]
        {
            "nvim_exec_lua",
            new Dictionary<string, object?>
            {
                ["text"] = new string('x', 300),
                ["blob"] = new byte[300],
                ["handles"] = Arr(new NvimHandle(0, 1), new NvimHandle(1, 70000)),
                ["nested"] = new Dictionary<string, object?> { ["list"] = Arr(1, -1, 40000, -40000, 5_000_000_000, ulong.MaxValue, 1.5, 2.5f, null, true) },
                ["big"] = Enumerable.Range(0, 20).Cast<object?>().ToArray(),
            },
            new string('y', 40),
        };
        return MsgPackRpc.EncodeRequest(123456, "nvim_call_function", args);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private static ReadOnlySequence<byte> Segmented(byte[] data, int chunk)
    {
        Segment? first = null;
        Segment? last = null;
        for (var offset = 0; offset < data.Length; offset += chunk)
        {
            var piece = new ReadOnlyMemory<byte>(data, offset, Math.Min(chunk, data.Length - offset));
            last = last is null ? first = new Segment(piece) : last.Append(piece);
        }

        return first is null ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    [Test]
    public void PartialFrame_ReturnsFalseAtEveryPrefix_ThenDecodesWhenComplete()
    {
        var frame = ComplexFrame();

        for (var length = 0; length < frame.Length; length++)
        {
            var single = new ReadOnlySequence<byte>(frame, 0, length);
            Assert.That(MsgPackRpc.TryDecode(ref single, out var message), Is.False, $"single segment, {length} bytes");
            Assert.That(message, Is.Null);
            Assert.That(single.Length, Is.EqualTo(length), "buffer untouched");

            // Same bytes split across two segments at every possible boundary.
            var split = Segmented(frame.AsSpan(0, length).ToArray(), Math.Max(1, length / 2));
            Assert.That(MsgPackRpc.TryDecode(ref split, out _), Is.False, $"two segments, {length} bytes");
        }

        var whole = new ReadOnlySequence<byte>(frame);
        Assert.That(MsgPackRpc.TryDecode(ref whole, out var decoded), Is.True);
        Assert.That(whole.IsEmpty, Is.True);
        Assert.That(decoded, Is.InstanceOf<RpcRequest>());
        Assert.That(((RpcRequest)decoded!).MsgId, Is.EqualTo(123456u));
    }

    [Test]
    public void FrameSplitIntoOneByteSegments_Decodes()
    {
        var frame = ComplexFrame();
        var oneByte = Segmented(frame, 1);

        Assert.That(MsgPackRpc.TryDecode(ref oneByte, out var message), Is.True);
        Assert.That(oneByte.IsEmpty, Is.True);
        var direct = (RpcRequest)DecodeOne(frame);
        var segmented = (RpcRequest)message!;
        Assert.That(segmented.Method, Is.EqualTo(direct.Method));
        AssertSame(direct.Args.ToArray(), segmented.Args.ToArray());
    }

    [Test]
    public void TwoFramesInOneBuffer_DecodeInOrder_AndTheTrailingPartialStays()
    {
        var a = MsgPackRpc.EncodeNotification("first", [1]);
        var b = MsgPackRpc.EncodeResponse(9, null, "second");
        var c = MsgPackRpc.EncodeRequest(3, "third", ["x"]);
        var buffer = new ReadOnlySequence<byte>(a.Concat(b).Concat(c.Take(c.Length - 2)).ToArray());

        Assert.That(MsgPackRpc.TryDecode(ref buffer, out var m1), Is.True);
        Assert.That(MsgPackRpc.TryDecode(ref buffer, out var m2), Is.True);
        Assert.That(MsgPackRpc.TryDecode(ref buffer, out var m3), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(m1, Is.InstanceOf<RpcNotification>());
            Assert.That(((RpcNotification)m1!).Method, Is.EqualTo("first"));
            Assert.That(m2, Is.InstanceOf<RpcResponse>());
            Assert.That(((RpcResponse)m2!).Result, Is.EqualTo("second"));
            Assert.That(m3, Is.Null);
            Assert.That(buffer.Length, Is.EqualTo(c.Length - 2));
        });

        var completed = new ReadOnlySequence<byte>(c);
        Assert.That(MsgPackRpc.TryDecode(ref completed, out var m4), Is.True);
        Assert.That(((RpcRequest)m4!).Method, Is.EqualTo("third"));
    }

    [Test]
    public void EmptyBuffer_NeedsMoreBytes()
    {
        var empty = ReadOnlySequence<byte>.Empty;

        Assert.That(MsgPackRpc.TryDecode(ref empty, out var message), Is.False);
        Assert.That(message, Is.Null);
    }

    public static IEnumerable<TestCaseData> MalformedFrames()
    {
        yield return new TestCaseData(new byte[] { 0x92, 0x09, 0x01 }).SetName("Malformed_unknown_frame_type");
        yield return new TestCaseData(new byte[] { 0x93, 0x00, 0x01, 0xa1, (byte)'m' }).SetName("Malformed_request_with_three_elements");
        yield return new TestCaseData(new byte[] { 0x05 }).SetName("Malformed_top_level_not_an_array");
        yield return new TestCaseData(new byte[] { 0x94, 0x00, 0x01, 0xa1, (byte)'m', 0x05 }).SetName("Malformed_params_not_an_array");
        yield return new TestCaseData(new byte[] { 0x94, 0x00, 0x01, 0x05, 0x90 }).SetName("Malformed_method_not_a_string");
        yield return new TestCaseData(new byte[] { 0x94, 0x00, 0x01, 0xa1, (byte)'m', 0x91, 0xc1 }).SetName("Malformed_reserved_code_0xc1");
        yield return new TestCaseData(new byte[] { 0x94, 0x00, 0x00, 0xa1, (byte)'m', 0x91, 0xd4, 0x7f, 0x00 }).SetName("Malformed_unknown_ext_type");
        yield return new TestCaseData(new byte[] { 0x94, 0x00, 0xff, 0xa1, (byte)'m', 0x90 }).SetName("Malformed_negative_msgid");
        yield return new TestCaseData(new byte[] { 0x94, 0x00, 0x01, 0xa1, (byte)'m', 0x91, 0x81, 0xc0, 0x01 }).SetName("Malformed_nil_map_key");
    }

    [TestCaseSource(nameof(MalformedFrames))]
    public void MalformedCompleteFrame_ThrowsFormatException_AndLeavesTheBufferUntouched(byte[] bytes)
    {
        var buffer = new ReadOnlySequence<byte>(bytes);

        Assert.That(() => MsgPackRpc.TryDecode(ref buffer, out _), Throws.TypeOf<FormatException>());
        Assert.That(buffer.Length, Is.EqualTo(bytes.Length));
    }

    [Test]
    public void Encode_RejectsValuesWithNoMapping()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => MsgPackRpc.EncodeRequest(1, "m", [Guid.NewGuid()]), Throws.ArgumentException);
            Assert.That(() => MsgPackRpc.EncodeRequest(1, "m", [new NvimHandle(5, 1)]), Throws.ArgumentException);
            Assert.That(() => MsgPackRpc.EncodeNotification("n", [new object()]), Throws.ArgumentException);
            Assert.That(() => MsgPackRpc.EncodeResponse(1, null, 1m), Throws.ArgumentException);
        });
    }

    [Test]
    public void Encode_RejectsRunawayNesting()
    {
        object? nested = "leaf";
        for (var i = 0; i < 300; i++)
            nested = new object?[] { nested };

        Assert.That(() => MsgPackRpc.EncodeRequest(1, "m", [nested]), Throws.ArgumentException);
    }
}
