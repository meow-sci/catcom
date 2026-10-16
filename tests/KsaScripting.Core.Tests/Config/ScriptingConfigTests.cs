using System.Reflection;
using KsaScripting.Core.Config;
using KsaScripting.Core.Logging;
using Tomlyn;

namespace KsaScripting.Core.Tests.Config;

[TestFixture]
[NonParallelizable]
public sealed class ScriptingConfigTests
{
    private static readonly string[] ExpectedKeys =
    [
        "frame_budget_ms", "sync_hook_budget_ms", "call_budget_ms", "fault_limit", "control_all_vessels",
        "debug_namespace", "autostart", "editor_enabled", "editor_port_min", "editor_port_max", "editor_theme",
        "v8_enabled", "lua_enabled", "v8_heap_limit_mb", "v8_debug_port", "lua_jit_enabled", "lua_memory_limit_kb",
        "terminal_output_buffer_kib", "manager_hotkey",
    ];

    private string _dir = null!;
    private MemoryLogSink _sink = null!;
    private ILogSink _previousSink = null!;
    private LogLevel _previousMinLevel;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ksa-scripting-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _sink = new MemoryLogSink();
        _previousSink = Log.SetSink(_sink);
        _previousMinLevel = Log.MinLevel;
        Log.MinLevel = LogLevel.Debug;
    }

    [TearDown]
    public void TearDown()
    {
        Log.SetSink(_previousSink);
        Log.MinLevel = _previousMinLevel;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string PathIn(string name) => Path.Combine(_dir, name);

    /// <summary>Every public property as a stable "Name=value" line, so two configs can be compared field by field.</summary>
    private static string Dump(ScriptingConfig config)
        => string.Join('\n', typeof(ScriptingConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.GetValue(config) is IEnumerable<string> list
                ? $"{p.Name}=[{string.Join(',', list)}]"
                : $"{p.Name}={p.GetValue(config)}"));

    private static ScriptingConfig NonDefault() => new()
    {
        FrameBudgetMs = 3.5,
        SyncHookBudgetMs = 7.25,
        CallBudgetMs = 120,
        FaultLimit = 9,
        ControlAllVessels = false,
        DebugNamespace = false,
        Autostart = ["ascent", "lib/tui"],
        EditorEnabled = false,
        EditorPortMin = 50000,
        EditorPortMax = 50010,
        EditorTheme = "solarized \"dark\"",
        V8Enabled = false,
        LuaEnabled = false,
        V8HeapLimitMb = 128,
        V8DebugPort = 9229,
        LuaJitEnabled = false,
        LuaMemoryLimitKb = 131072,
        TerminalOutputBufferKib = 512,
        ManagerHotkey = "F10",
    };

    // ---------------------------------------------------------------- defaults and format

    [Test]
    public void Defaults_MatchTheDocumentedValues()
    {
        var c = new ScriptingConfig();
        Assert.Multiple(() =>
        {
            Assert.That(c.FrameBudgetMs, Is.EqualTo(2.0));
            Assert.That(c.SyncHookBudgetMs, Is.EqualTo(5.0));
            Assert.That(c.CallBudgetMs, Is.EqualTo(50.0));
            Assert.That(c.FaultLimit, Is.EqualTo(3));
            Assert.That(c.ControlAllVessels, Is.True);
            Assert.That(c.DebugNamespace, Is.True);
            Assert.That(c.Autostart, Is.Empty);
            Assert.That(c.EditorEnabled, Is.True);
            Assert.That(c.EditorPortMin, Is.EqualTo(48600));
            Assert.That(c.EditorPortMax, Is.EqualTo(48699));
            Assert.That(c.EditorTheme, Is.EqualTo(""));
            Assert.That(c.V8Enabled, Is.True);
            Assert.That(c.LuaEnabled, Is.True);
            Assert.That(c.V8HeapLimitMb, Is.EqualTo(64));
            Assert.That(c.V8DebugPort, Is.EqualTo(0));
            Assert.That(c.LuaJitEnabled, Is.True);
            Assert.That(c.LuaMemoryLimitKb, Is.EqualTo(65536));
            Assert.That(c.TerminalOutputBufferKib, Is.EqualTo(256));
            Assert.That(c.ManagerHotkey, Is.EqualTo("F11"));
            Assert.That(ScriptingConfig.FileName, Is.EqualTo("ksa-scripting.toml"));
        });
    }

    [Test]
    public void ToToml_ContainsEachOfTheNineteenExactKeys()
    {
        var toml = new ScriptingConfig().ToToml();
        var keys = toml.Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith('#') && l.Contains('='))
            .Select(l => l[..l.IndexOf('=')].Trim())
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(toml, Does.StartWith("# ksa-scripting configuration"));
            Assert.That(keys, Is.EquivalentTo(ExpectedKeys), "exactly the documented keys, no more, no fewer");
            Assert.That(keys, Has.Length.EqualTo(19));
        });
    }

    [Test]
    public void ToToml_RendersDefaultsAsTomlValues()
    {
        var toml = new ScriptingConfig().ToToml();
        Assert.Multiple(() =>
        {
            Assert.That(toml, Does.Contain("fault_limit = 3"));
            Assert.That(toml, Does.Contain("v8_enabled = true"));
            Assert.That(toml, Does.Contain("autostart = []"));
            Assert.That(toml, Does.Contain("manager_hotkey = \"F11\""));
            Assert.That(toml, Does.Contain("editor_port_min = 48600"));
        });
    }

    [Test]
    public void ToToml_ThenFromToml_RoundTripsNonDefaultValues()
    {
        var original = NonDefault();
        var parsed = ScriptingConfig.FromToml(original.ToToml());
        Assert.That(Dump(parsed), Is.EqualTo(Dump(original)));
    }

    [Test]
    public void FromToml_MissingKeysKeepDefaults_AndUnknownKeysAreIgnored()
    {
        var parsed = ScriptingConfig.FromToml("fault_limit = 7\nsome_future_key = \"x\"\n");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.FaultLimit, Is.EqualTo(7));
            Assert.That(parsed.FrameBudgetMs, Is.EqualTo(2.0));
            Assert.That(parsed.ManagerHotkey, Is.EqualTo("F11"));
        });
    }

    [Test]
    public void FromToml_EmptyDocument_IsDefaults()
        => Assert.That(Dump(ScriptingConfig.FromToml("")), Is.EqualTo(Dump(new ScriptingConfig())));

    [Test]
    public void FromToml_Malformed_Throws()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => ScriptingConfig.FromToml("fault_limit = = 3"), Throws.InstanceOf<TomlException>());
            Assert.That(() => ScriptingConfig.FromToml("fault_limit = \"three\""), Throws.InstanceOf<TomlException>());
        });
    }

    [Test]
    public void FromToml_AcceptsHandWrittenSnakeCaseKeys()
    {
        var parsed = ScriptingConfig.FromToml(
            "v8_heap_limit_mb = 99\nlua_memory_limit_kb = 2048\nterminal_output_buffer_kib = 8\nautostart = [\"a\", \"b\"]\n");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.V8HeapLimitMb, Is.EqualTo(99));
            Assert.That(parsed.LuaMemoryLimitKb, Is.EqualTo(2048));
            Assert.That(parsed.TerminalOutputBufferKib, Is.EqualTo(8));
            Assert.That(parsed.Autostart, Is.EqualTo(new[] { "a", "b" }));
        });
    }

    // ---------------------------------------------------------------- Normalize

    [Test]
    public void Normalize_OnDefaults_ChangesNothingAndWarnsNothing()
    {
        var config = new ScriptingConfig();
        var before = Dump(config);

        var warnings = config.Normalize();

        Assert.Multiple(() =>
        {
            Assert.That(warnings, Is.Empty);
            Assert.That(Dump(config), Is.EqualTo(before));
            Assert.That(_sink.Entries, Is.Empty);
        });
    }

    [Test]
    public void Normalize_ClampsEverythingTooLow()
    {
        var config = new ScriptingConfig
        {
            FrameBudgetMs = 0,
            SyncHookBudgetMs = -5,
            CallBudgetMs = 0.5,
            FaultLimit = 0,
            EditorPortMin = 80,
            EditorPortMax = 90,
            V8HeapLimitMb = 1,
            LuaMemoryLimitKb = 1,
            TerminalOutputBufferKib = 0,
        };

        var warnings = config.Normalize();

        Assert.Multiple(() =>
        {
            Assert.That(config.FrameBudgetMs, Is.EqualTo(0.1));
            Assert.That(config.SyncHookBudgetMs, Is.EqualTo(0.1));
            Assert.That(config.CallBudgetMs, Is.EqualTo(1));
            Assert.That(config.FaultLimit, Is.EqualTo(1));
            Assert.That(config.EditorPortMin, Is.EqualTo(1024));
            Assert.That(config.EditorPortMax, Is.EqualTo(1024));
            Assert.That(config.V8HeapLimitMb, Is.EqualTo(8));
            Assert.That(config.LuaMemoryLimitKb, Is.EqualTo(1024));
            Assert.That(config.TerminalOutputBufferKib, Is.EqualTo(4));
            Assert.That(warnings, Has.Count.EqualTo(9));
        });
    }

    [Test]
    public void Normalize_ClampsEverythingTooHigh()
    {
        var config = new ScriptingConfig
        {
            FrameBudgetMs = 1000,
            SyncHookBudgetMs = 1e9,
            CallBudgetMs = double.PositiveInfinity,
            FaultLimit = 5000,
            EditorPortMin = 70000,
            EditorPortMax = 99999,
            V8HeapLimitMb = 100000,
            LuaMemoryLimitKb = int.MaxValue,
            TerminalOutputBufferKib = 1_000_000,
        };

        config.Normalize();

        Assert.Multiple(() =>
        {
            Assert.That(config.FrameBudgetMs, Is.EqualTo(100));
            Assert.That(config.SyncHookBudgetMs, Is.EqualTo(1000));
            Assert.That(config.CallBudgetMs, Is.EqualTo(10000));
            Assert.That(config.FaultLimit, Is.EqualTo(1000));
            Assert.That(config.EditorPortMin, Is.EqualTo(65535));
            Assert.That(config.EditorPortMax, Is.EqualTo(65535));
            Assert.That(config.V8HeapLimitMb, Is.EqualTo(4096));
            Assert.That(config.LuaMemoryLimitKb, Is.EqualTo(4194304));
            Assert.That(config.TerminalOutputBufferKib, Is.EqualTo(65536));
        });
    }

    [Test]
    public void Normalize_ReturnsTheWarnings_AndLogsEachOneAtWarnLevel()
    {
        var config = new ScriptingConfig { FrameBudgetMs = 500, FaultLimit = -1 };

        var warnings = config.Normalize();

        Assert.Multiple(() =>
        {
            Assert.That(warnings, Has.Count.EqualTo(2));
            Assert.That(warnings[0], Does.Contain("frame_budget_ms").And.Contain("500").And.Contain("100"));
            Assert.That(warnings[1], Does.Contain("fault_limit").And.Contain("-1"));
            Assert.That(_sink.Entries.Select(e => e.Level), Is.All.EqualTo(LogLevel.Warn));
            Assert.That(_sink.Entries.Select(e => e.Message), Is.EqualTo(warnings));
        });
    }

    [Test]
    public void Normalize_NaNDoubles_FallBackToTheDefaults()
    {
        var config = new ScriptingConfig { FrameBudgetMs = double.NaN, SyncHookBudgetMs = double.NaN, CallBudgetMs = double.NaN };

        var warnings = config.Normalize();

        Assert.Multiple(() =>
        {
            Assert.That(config.FrameBudgetMs, Is.EqualTo(2.0));
            Assert.That(config.SyncHookBudgetMs, Is.EqualTo(5.0));
            Assert.That(config.CallBudgetMs, Is.EqualTo(50.0));
            Assert.That(warnings, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void Normalize_InvertedPortRange_ResetsBothToTheDefaults()
    {
        var config = new ScriptingConfig { EditorPortMin = 60000, EditorPortMax = 50000 };

        var warnings = config.Normalize();

        Assert.Multiple(() =>
        {
            Assert.That(config.EditorPortMin, Is.EqualTo(48600));
            Assert.That(config.EditorPortMax, Is.EqualTo(48699));
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("editor_port_min").And.Contain("editor_port_max"));
        });
    }

    [Test]
    public void Normalize_EqualPorts_AreFine()
    {
        var config = new ScriptingConfig { EditorPortMin = 5000, EditorPortMax = 5000 };
        Assert.Multiple(() =>
        {
            Assert.That(config.Normalize(), Is.Empty);
            Assert.That(config.EditorPortMin, Is.EqualTo(5000));
        });
    }

    [TestCase(0, 0, false)]
    [TestCase(1024, 1024, false)]
    [TestCase(9229, 9229, false)]
    [TestCase(65535, 65535, false)]
    [TestCase(1, 0, true)]
    [TestCase(1023, 0, true)]
    [TestCase(65536, 0, true)]
    [TestCase(-5, 0, true)]
    public void Normalize_V8DebugPort_IsZeroOrAValidPort(int port, int expected, bool warns)
    {
        var config = new ScriptingConfig { V8DebugPort = port };

        var warnings = config.Normalize();

        Assert.Multiple(() =>
        {
            Assert.That(config.V8DebugPort, Is.EqualTo(expected));
            Assert.That(warnings.Count > 0, Is.EqualTo(warns));
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void Normalize_BlankHotkey_BecomesF11(string? hotkey)
    {
        var config = new ScriptingConfig { ManagerHotkey = hotkey! };

        var warnings = config.Normalize();

        Assert.Multiple(() =>
        {
            Assert.That(config.ManagerHotkey, Is.EqualTo("F11"));
            Assert.That(warnings, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Normalize_Autostart_IsTrimmedDeduplicatedAndNeverNull()
    {
        var config = new ScriptingConfig { Autostart = ["  ascent ", "", "   ", "ascent", "Ascent", "lib/tui", null!, "lib/tui "] };

        config.Normalize();

        Assert.That(config.Autostart, Is.EqualTo(new[] { "ascent", "Ascent", "lib/tui" }), "ordinal, case-sensitive de-duplication, first wins");

        var nullList = new ScriptingConfig { Autostart = null! };
        nullList.Normalize();
        Assert.That(nullList.Autostart, Is.Not.Null.And.Empty);
    }

    // ---------------------------------------------------------------- LoadOrCreate

    [Test]
    public void LoadOrCreate_MissingFile_CreatesItWithDefaults()
    {
        var path = PathIn(ScriptingConfig.FileName);

        var config = ScriptingConfig.LoadOrCreate(path);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(path), Is.True);
            Assert.That(Dump(config), Is.EqualTo(Dump(new ScriptingConfig())));
            Assert.That(Dump(ScriptingConfig.FromToml(File.ReadAllText(path))), Is.EqualTo(Dump(new ScriptingConfig())));
            Assert.That(Directory.GetFiles(_dir, "*.tmp"), Is.Empty);
        });
    }

    [Test]
    public void LoadOrCreate_MissingFileInMissingDirectory_CreatesBoth()
    {
        var path = Path.Combine(_dir, "nested", "deeper", ScriptingConfig.FileName);

        var config = ScriptingConfig.LoadOrCreate(path);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(path), Is.True);
            Assert.That(config.FaultLimit, Is.EqualTo(3));
        });
    }

    [Test]
    public void LoadOrCreate_UnwritableLocation_StillReturnsDefaults_AndWarns()
    {
        // The parent "directory" is a regular file, so creating the config under it must fail.
        var blocker = PathIn("blocker");
        File.WriteAllText(blocker, "x");

        var config = ScriptingConfig.LoadOrCreate(Path.Combine(blocker, ScriptingConfig.FileName));

        Assert.Multiple(() =>
        {
            Assert.That(Dump(config), Is.EqualTo(Dump(new ScriptingConfig())));
            Assert.That(_sink.Entries.Any(e => e.Level == LogLevel.Warn), Is.True);
        });
    }

    [Test]
    public void LoadOrCreate_MalformedToml_ReturnsDefaults_AndLeavesTheFileByteIdentical()
    {
        var path = PathIn(ScriptingConfig.FileName);
        byte[] original = [.. "fault_limit = = nope\n[unterminated\n"u8];
        File.WriteAllBytes(path, original);

        var config = ScriptingConfig.LoadOrCreate(path);

        Assert.Multiple(() =>
        {
            Assert.That(Dump(config), Is.EqualTo(Dump(new ScriptingConfig())));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
            Assert.That(_sink.Entries.Any(e => e.Level == LogLevel.Warn && e.Message.Contains(path)), Is.True);
            Assert.That(Directory.GetFiles(_dir, "*.tmp"), Is.Empty);
        });
    }

    [Test]
    public void LoadOrCreate_WrongValueType_ReturnsDefaults_AndLeavesTheFile()
    {
        var path = PathIn(ScriptingConfig.FileName);
        const string text = "fault_limit = \"lots\"\n";
        File.WriteAllText(path, text);

        var config = ScriptingConfig.LoadOrCreate(path);

        Assert.Multiple(() =>
        {
            Assert.That(config.FaultLimit, Is.EqualTo(3));
            Assert.That(File.ReadAllText(path), Is.EqualTo(text));
        });
    }

    [Test]
    public void LoadOrCreate_ExistingFile_IsReadAndNormalized_WithoutRewritingIt()
    {
        var path = PathIn(ScriptingConfig.FileName);
        const string text = "frame_budget_ms = 9999\nfault_limit = 5\nautostart = [\"a\", \"a\"]\n";
        File.WriteAllText(path, text);

        var config = ScriptingConfig.LoadOrCreate(path);

        Assert.Multiple(() =>
        {
            Assert.That(config.FrameBudgetMs, Is.EqualTo(100), "clamped in memory");
            Assert.That(config.FaultLimit, Is.EqualTo(5));
            Assert.That(config.Autostart, Is.EqualTo(new[] { "a" }));
            Assert.That(File.ReadAllText(path), Is.EqualTo(text), "the user's file is not rewritten");
            Assert.That(_sink.Entries.Any(e => e.Message.Contains("frame_budget_ms")), Is.True);
        });
    }

    [Test]
    public void Save_ThenLoadOrCreate_RoundTrips()
    {
        var path = PathIn(ScriptingConfig.FileName);
        var original = NonDefault();

        original.Save(path);
        var loaded = ScriptingConfig.LoadOrCreate(path);

        Assert.Multiple(() =>
        {
            Assert.That(Dump(loaded), Is.EqualTo(Dump(original)));
            Assert.That(Directory.GetFiles(_dir, "*.tmp"), Is.Empty);
        });
    }

    // ---------------------------------------------------------------- AtomicFile

    [Test]
    public void AtomicFile_WriteAllText_WritesThenOverwrites_AndLeavesNoTempFile()
    {
        var path = PathIn("a.txt");

        AtomicFile.WriteAllText(path, "first");
        Assert.That(File.ReadAllText(path), Is.EqualTo("first"));

        AtomicFile.WriteAllText(path, "second, longer");
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(path), Is.EqualTo("second, longer"));
            Assert.That(Directory.GetFiles(_dir), Is.EqualTo(new[] { path }), "no .tmp left behind");
        });
    }

    [Test]
    public void AtomicFile_WriteAllBytes_WritesThenOverwrites()
    {
        var path = PathIn("b.bin");

        AtomicFile.WriteAllBytes(path, [1, 2, 3, 4]);
        AtomicFile.WriteAllBytes(path, [9]);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 9 }));
            Assert.That(Directory.GetFiles(_dir, "*.tmp"), Is.Empty);
        });
    }

    [Test]
    public void AtomicFile_CreatesTheParentDirectory()
    {
        var path = Path.Combine(_dir, "x", "y", "c.txt");

        AtomicFile.WriteAllText(path, "hi");

        Assert.That(File.ReadAllText(path), Is.EqualTo("hi"));
    }

    [Test]
    public void AtomicFile_FailedWrite_ThrowsAndLeavesNoTempFile()
    {
        // The destination is an existing DIRECTORY, so the final move cannot succeed.
        var path = PathIn("dest");
        Directory.CreateDirectory(path);

        Assert.That(() => AtomicFile.WriteAllText(path, "x"), Throws.Exception);

        Assert.That(File.Exists(path + ".tmp"), Is.False);
    }
}
