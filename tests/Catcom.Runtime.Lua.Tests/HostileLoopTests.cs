using Catcom.Core.Runtime;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>Runaway scripts that try to hide from the interrupt behind protected calls, handlers and metamethods.</summary>
[TestFixture]
public sealed class HostileLoopTests
{
    private static readonly (string Name, string Body)[] Loops =
    [
        ("plain", "while true do end"),
        ("repeat-until", "local n = 0 repeat n = n + 1 until false"),
        ("numeric-for", "for i = 1, math.huge do end"),
        ("pcall-in-loop", "while true do pcall(function() while true do end end) end"),
        ("pcall-nested", "pcall(pcall, pcall, function() while true do end end) while true do end"),
        ("xpcall-handler-loops", "xpcall(function() error('x') end, function() while true do end end) while true do end"),
        ("xpcall-swallows", "while true do xpcall(function() while true do end end, function(m) return m end) end"),
        ("coroutine-resume", "while true do coroutine.resume(coroutine.create(function() while true do end end)) end"),
        ("coroutine-wrap-pcall", "while true do pcall(coroutine.wrap(function() while true do end end)) end"),
        ("index-metamethod", "local t = setmetatable({}, { __index = function() while true do end end }) local _ = t.x"),
        ("call-metamethod", "local t = setmetatable({}, { __call = function() while true do end end }) t()"),
        ("sort-comparator", "local t = {} for i = 1, 100 do t[i] = i end table.sort(t, function() while true do end end)"),
        ("gsub-callback", "string.gsub('abc', '.', function() while true do end end)"),
        ("tostring-metamethod", "local t = setmetatable({}, { __tostring = function() while true do end end }) tostring(t)"),
        ("c-function-calls", "local s = 'abc' while true do local _ = s:byte(1) + #s:upper() end"),
        ("table-churn", "while true do local t = {} for i = 1, 10 do t[i] = {i} end end"),
        ("error-churn", "while true do pcall(error, 'x') end"),
        ("select-varargs", "local function f(...) return select('#', ...) end while true do f(1, 2, 3) end"),
    ];

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (var jit in new[] { true, false })
        {
            foreach (var (name, body) in Loops)
                yield return new TestCaseData(jit, body).SetName($"HostileLoop_{name}_Jit{(jit ? "On" : "Off")}_IsInterrupted");
        }
    }

    [TestCaseSource(nameof(Cases))]
    [CancelAfter(30000)]
    public void HostileLoop_IsInterrupted(bool jit, string body)
    {
        using var fx = new LuaRuntimeFixture(jit);
        var spinning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Host.OnLog = log =>
        {
            if (log.Message == "spinning")
                spinning.TrySetResult();
        };
        var module = fx.Load("hostile", $"return {{ spin = function() ksa_host.log(1, 'spinning') {body} end, ok = function() return 'alive' end }}");
        using var interrupter = new BackgroundInterrupter(fx.Runtime, spinning.Task);

        var ex = Assert.Throws<ScriptRuntimeException>(() => fx.Runtime.Invoke(fx.Export(module, "spin"), []))!;
        interrupter.Finish();

        Assert.That(ex.Message, Is.EqualTo("interrupted"));
        Assert.That(fx.Runtime.Invoke(fx.Export(module, "ok"), []).AsString(), Is.EqualTo("alive"));
    }
}
