using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>
///     The shared terminal-input parser vectors (<c>tui-vectors.json</c>, also run against the TypeScript implementation)
///     driven through the real LuaJIT runtime: the unmodified <c>scripts-lib/tui.lua</c> is loaded with <c>require</c> inside
///     the sandbox, chunks cross as raw bytes, and the produced events are compared with the expected ones.
/// </summary>
[TestFixture]
public sealed class TuiParserVectorTests
{
    // Runs inside the script sandbox. Binary-safe: every string field that can hold bytes that are not valid UTF-8
    // (or that the vectors compare as bytes) is hex-encoded here, so the ScriptValue String conversion cannot alter it.
    private const string HarnessSource = """
        local tui = require("lib.tui")

        local function hex(s)
          return (string.gsub(s, ".", function(c) return string.format("%02x", string.byte(c)) end))
        end

        local HEX_FIELDS = { name = true, text = true, bytes = true }

        local function describe(ev)
          local out = {}
          for k, v in pairs(ev) do
            if HEX_FIELDS[k] and type(v) == "string" then
              out[k] = hex(v)
            elseif type(v) == "table" then
              local copy = {}
              for k2, v2 in pairs(v) do copy[k2] = v2 end
              out[k] = copy
            else
              out[k] = v
            end
          end
          return out
        end

        return {
          feed = function(chunks, flush_at_end, expect_cpr)
            local parser = tui.InputParser.new()
            if type(expect_cpr) == "number" and expect_cpr > 0 then parser:expect_cpr(expect_cpr) end
            local events, n = {}, 0
            local function take(list)
              for i = 1, #list do
                n = n + 1
                events[n] = describe(list[i])
              end
            end
            for i = 1, #chunks do take(parser:feed(chunks[i])) end
            if flush_at_end then take(parser:flush()) end
            return { n = n, events = events }
          end,
        }
        """;

    private LuaRuntimeFixture _fixture = null!;
    private ScriptCallable _feed = null!;

    /// <summary>Every vector as its own test case, named after the vector.</summary>
    /// <returns>The test cases.</returns>
    public static IEnumerable<TestCaseData> Vectors()
    {
        foreach (var vector in TuiVector.LoadAll())
            yield return new TestCaseData(vector).SetName(vector.Name);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _fixture = new LuaRuntimeFixture();
        _feed = LoadHarness(_fixture);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _feed.Dispose();
        _fixture.Dispose();
    }

    private static ScriptCallable LoadHarness(LuaRuntimeFixture fixture)
    {
        TuiTestSupport.InstallLibrary(fixture);
        return fixture.Export(fixture.Load("tui-harness", HarnessSource), "feed");
    }

    // Feeds a vector through the harness and returns the produced events in canonical form.
    private static string[] Run(LuaRuntimeFixture fixture, ScriptCallable feed, TuiVector vector)
    {
        var chunks = ScriptValue.List(vector.Chunks.Select(c => ScriptValue.From(c)).ToArray());
        var cpr = vector.ExpectCpr is { } expected ? ScriptValue.From((double)expected) : ScriptValue.Null;

        var result = fixture.Runtime.Invoke(feed, [chunks, ScriptValue.From(vector.FlushAtEnd), cpr]);

        // An empty Lua table crosses as an empty Map, a non-empty sequence as a List.
        var events = (int)result["n"].AsNumber() == 0 ? [] : result["events"].AsList();
        return events.Select(e => TuiEventForm.FromActual(e.AsMap())).ToArray();
    }

    [Test]
    public void VectorFile_IsComplete()
    {
        var vectors = TuiVector.LoadAll();

        Assert.That(vectors, Has.Count.EqualTo(380));
        Assert.That(vectors.Select(v => v.Name).Distinct().Count(), Is.EqualTo(vectors.Count), "vector names are unique");
    }

    [TestCaseSource(nameof(Vectors))]
    public void Parser_ProducesTheExpectedEvents(TuiVector vector)
    {
        Assert.That(Run(_fixture, _feed, vector), Is.EqualTo(vector.Expected));
        Assert.That(_fixture.Host.ReportedErrors, Is.Empty);
    }

    // The reference harness's rule: a chunk boundary may fall anywhere except right after ESC, or after the [ / O of ESC [ / ESC O
    // (those boundaries change the meaning by design: a lone Escape key, or Alt+[ / Alt+O).
    private static bool IsAllowedSplit(byte[] data, int position)
    {
        if (position <= 0 || position >= data.Length)
            return false;
        var previous = data[position - 1];
        if (previous == 0x1b)
            return false;
        return !((previous == 0x5b || previous == 0x4f) && position >= 2 && data[position - 2] == 0x1b);
    }

    [Test]
    public void ChunkBoundaries_DoNotChangeTheEvents_ForEverySingleChunkVector()
    {
        var checkedSplits = 0;
        foreach (var vector in TuiVector.LoadAll().Where(v => v.Chunks.Length == 1))
        {
            var data = vector.Chunks[0];
            var allowed = Enumerable.Range(1, Math.Max(0, data.Length - 1)).Where(p => IsAllowedSplit(data, p)).ToArray();

            foreach (var position in allowed)
            {
                var split = vector with { Chunks = [data[..position], data[position..]] };
                Assert.That(Run(_fixture, _feed, split), Is.EqualTo(vector.Expected), $"{vector.Name} split at {position}");
                checkedSplits++;
            }

            // Every allowed boundary at once.
            var pieces = new List<byte[]>();
            var start = 0;
            foreach (var position in allowed)
            {
                pieces.Add(data[start..position]);
                start = position;
            }

            pieces.Add(data[start..]);
            Assert.That(Run(_fixture, _feed, vector with { Chunks = [.. pieces] }), Is.EqualTo(vector.Expected), $"{vector.Name} split everywhere");
            checkedSplits++;
        }

        Assert.That(checkedSplits, Is.GreaterThan(1000));
    }

    [Test]
    public void EveryVector_AlsoPasses_WithTheJitDisabled_AndWhenRepeatedUntilTheJitHasCompiledTheParser()
    {
        using var interpreted = new LuaRuntimeFixture(jitEnabled: false);
        using var feed = LoadHarness(interpreted);
        var vectors = TuiVector.LoadAll();

        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var vector in vectors)
                Assert.That(Run(interpreted, feed, vector), Is.EqualTo(vector.Expected), vector.Name);
        }

        // The same vectors, repeated many times on the JIT-enabled runtime (hot loops get compiled to machine code).
        foreach (var vector in vectors.Take(60))
        {
            for (var i = 0; i < 150; i++)
                Assert.That(Run(_fixture, _feed, vector), Is.EqualTo(vector.Expected), vector.Name);
        }

        Assert.That(interpreted.Host.ReportedErrors, Is.Empty);
    }
}
