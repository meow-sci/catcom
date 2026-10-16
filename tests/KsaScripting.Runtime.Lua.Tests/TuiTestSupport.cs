using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KsaScripting.Runtime.Lua.Tests;

/// <summary>Shared pieces of the terminal UI toolkit tests: where the linked library lives and how it is installed.</summary>
internal static class TuiTestSupport
{
    /// <summary>The parser vectors shared with the TypeScript implementation (copied to the test output).</summary>
    public static string VectorsPath => Path.Combine(AppContext.BaseDirectory, "TestData", "tui-vectors.json");

    /// <summary><c>scripts-lib/tui.lua</c>, linked into the test output (not duplicated in the repository).</summary>
    public static string LibraryPath => Path.Combine(AppContext.BaseDirectory, "TestData", "scripts-lib", "tui.lua");

    /// <summary>Copies the library to <c>&lt;ScriptsDir&gt;/lib/tui.lua</c>, where <c>require("lib.tui")</c> finds it.</summary>
    /// <param name="fixture">The fixture whose scripts directory receives the library.</param>
    public static void InstallLibrary(LuaRuntimeFixture fixture)
    {
        var target = Path.Combine(fixture.ScriptsDir, "lib", "tui.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(LibraryPath, target, overwrite: true);
    }

    /// <summary>Decodes a lowercase hex string.</summary>
    /// <param name="hex">The hex digits (two per byte).</param>
    /// <returns>The bytes.</returns>
    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    /// <summary>Renders bytes as lowercase hex followed by a printable preview, for readable assertion messages.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>For example <c>1b5b41 "\x1b[A"</c>.</returns>
    public static string Show(ReadOnlySpan<byte> bytes)
    {
        var preview = new StringBuilder("\"");
        foreach (var b in bytes)
        {
            if (b is >= 0x20 and < 0x7f && b != (byte)'"' && b != (byte)'\\')
                preview.Append((char)b);
            else
                preview.Append(CultureInfo.InvariantCulture, $"\\x{b:x2}");
        }

        preview.Append('"');
        return $"{Convert.ToHexString(bytes).ToLowerInvariant()} {preview}";
    }
}

/// <summary>One entry of <c>tui-vectors.json</c>, with the expected events already normalised (see <see cref="TuiEventForm"/>).</summary>
/// <param name="Name">The vector name (unique).</param>
/// <param name="Chunks">The input chunks as raw bytes (the JSON holds one latin1 character per byte).</param>
/// <param name="FlushAtEnd">Whether <c>flush()</c> is called after the last chunk.</param>
/// <param name="ExpectCpr">The <c>expect_cpr</c> count, if the vector sets one.</param>
/// <param name="Expected">The expected events in canonical form.</param>
public sealed record TuiVector(string Name, byte[][] Chunks, bool FlushAtEnd, int? ExpectCpr, string[] Expected)
{
    /// <summary>Reads every vector from <see cref="TuiTestSupport.VectorsPath"/>.</summary>
    /// <returns>The vectors in file order.</returns>
    public static IReadOnlyList<TuiVector> LoadAll()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(TuiTestSupport.VectorsPath));
        var vectors = new List<TuiVector>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var chunks = entry.GetProperty("chunks").EnumerateArray().Select(c => Encoding.Latin1.GetBytes(c.GetString()!)).ToArray();
            int? cpr = entry.TryGetProperty("expectCpr", out var cprElement) && cprElement.ValueKind == JsonValueKind.Number ? cprElement.GetInt32() : null;
            var flush = entry.TryGetProperty("flushAtEnd", out var flushElement) && flushElement.ValueKind == JsonValueKind.True;
            var expected = entry.GetProperty("events").EnumerateArray().Select(TuiEventForm.FromExpected).ToArray();
            vectors.Add(new TuiVector(entry.GetProperty("name").GetString()!, chunks, flush, cpr, expected));
        }

        return vectors;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
///     Canonical text for an input event, built from either side of the comparison so that the two can be compared as
///     plain strings: sorted <c>field=value</c> pairs, strings rendered as hex bytes, booleans and numbers invariant,
///     the <c>mods</c> table flattened to <c>mods.shift</c> ... A field present on one side only shows up as a difference.
/// </summary>
internal static class TuiEventForm
{
    // Lua spells these differently from the shared vectors (the TypeScript names).
    private static readonly Dictionary<string, string> LuaNames = new(StringComparer.Ordinal)
    {
        ["pageUp"] = "page_up",
        ["pageDown"] = "page_down",
        ["wheelUp"] = "wheel_up",
        ["wheelDown"] = "wheel_down",
        ["wheelLeft"] = "wheel_left",
        ["wheelRight"] = "wheel_right",
    };

    /// <summary>Builds the canonical form of one expected event from the vector file.</summary>
    /// <param name="element">The expected event object.</param>
    /// <returns>The canonical form.</returns>
    public static string FromExpected(JsonElement element)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "mods":
                    foreach (var mod in property.Value.EnumerateObject())
                        fields["mods." + mod.Name] = Bool(mod.Value.GetBoolean());
                    break;
                case "name":
                case "button":
                    var text = property.Value.GetString()!;
                    fields[property.Name] = TuiTestSupport.Show(Encoding.UTF8.GetBytes(LuaNames.GetValueOrDefault(text, text)));
                    break;
                case "text":
                    fields["text"] = TuiTestSupport.Show(Encoding.UTF8.GetBytes(property.Value.GetString()!));
                    break;
                case "bytes":
                    fields["bytes"] = TuiTestSupport.Show(Encoding.Latin1.GetBytes(property.Value.GetString()!)); // latin1: one char = one byte
                    break;
                case "paste":
                    fields["paste"] = Bool(property.Value.GetBoolean());
                    break;
                case "col":
                case "row":
                    fields[property.Name] = property.Value.GetDouble().ToString("R", CultureInfo.InvariantCulture);
                    break;
                default: // kind, action
                    fields[property.Name] = TuiTestSupport.Show(Encoding.UTF8.GetBytes(property.Value.GetString()!));
                    break;
            }
        }

        return Join(fields);
    }

    /// <summary>Builds the canonical form of one event returned by the Lua harness (hex-encoded string fields).</summary>
    /// <param name="fields">The event map.</param>
    /// <returns>The canonical form.</returns>
    public static string FromActual(IReadOnlyDictionary<string, KsaScripting.Core.Values.ScriptValue> fields)
    {
        var form = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            switch (key)
            {
                case "mods":
                    foreach (var (mod, flag) in value.AsMap())
                        form["mods." + mod] = Bool(flag.AsBool());
                    break;
                case "name":
                case "text":
                case "bytes":
                    form[key] = TuiTestSupport.Show(TuiTestSupport.FromHex(value.AsString())); // hex-encoded inside Lua
                    break;
                case "paste":
                    form[key] = Bool(value.AsBool());
                    break;
                case "col":
                case "row":
                    form[key] = value.AsNumber().ToString("R", CultureInfo.InvariantCulture);
                    break;
                default: // kind, button, action
                    form[key] = TuiTestSupport.Show(Encoding.UTF8.GetBytes(value.AsString()));
                    break;
            }
        }

        return Join(form);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Join(SortedDictionary<string, string> fields) => string.Join(" ", fields.Select(f => $"{f.Key}={f.Value}"));
}
