using System.Text.Json;
using KsaScripting.Core.Config;
using KsaScripting.Core.Logging;
using Tomlyn;

namespace KsaScripting.Core.Hooks;

/// <summary>
///     Persists UI-created bindings in <c>bindings.toml</c> as an array of <c>[[binding]]</c> tables
///     (keys <c>script</c>, <c>export</c>, <c>kind</c>, <c>key</c>, <c>sync</c>, <c>vessel</c> (omitted when
///     unset), <c>clock</c>, <c>interval_seconds</c>, <c>enabled</c>). Writes are atomic.
/// </summary>
public sealed class BindingStore
{
    private const string FileHeader =
        """
        # ksa-scripting bindings created in the Script Manager.
        # Each [[binding]] attaches an exported script function to an event, a tick or a command.

        """;

    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Creates a store over a file.</summary>
    /// <param name="path">The <c>bindings.toml</c> path; it need not exist yet.</param>
    public BindingStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
    }

    /// <summary>The <c>bindings.toml</c> path.</summary>
    public string Path { get; }

    /// <summary>
    ///     Reads the stored bindings. A missing file yields an empty list; a malformed file is logged
    ///     as a warning, yields an empty list and is left untouched.
    /// </summary>
    /// <returns>The stored records in file order.</returns>
    public IReadOnlyList<BindingRecord> Load()
    {
        string text;
        try
        {
            if (!File.Exists(Path))
                return [];
            text = File.ReadAllText(Path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read '{Path}': {ex.Message}");
            return [];
        }

        try
        {
            var file = TomlSerializer.Deserialize<BindingFile>(text, TomlOptions);
            var records = new List<BindingRecord>();
            foreach (var row in file?.Binding ?? [])
            {
                if (string.IsNullOrWhiteSpace(row.Script) || string.IsNullOrWhiteSpace(row.Export))
                {
                    Log.Warn($"Ignoring a binding in '{Path}' without a script or export.");
                    continue;
                }

                records.Add(new BindingRecord(
                    row.Script,
                    row.Export,
                    row.Kind ?? "event",
                    row.Key ?? "",
                    row.Sync,
                    string.IsNullOrWhiteSpace(row.Vessel) ? null : row.Vessel,
                    string.IsNullOrWhiteSpace(row.Clock) ? "ut" : row.Clock,
                    row.IntervalSeconds,
                    row.Enabled));
            }

            return records;
        }
        catch (Exception ex)
        {
            Log.Warn($"'{Path}' is not valid ({ex.Message}); ignoring its bindings. Fix or delete the file.");
            return [];
        }
    }

    /// <summary>Writes the records atomically (temp file + rename), replacing the file.</summary>
    /// <param name="records">The bindings to store.</param>
    public void Save(IReadOnlyList<BindingRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var file = new BindingFile();
        foreach (var record in records)
        {
            file.Binding.Add(new BindingRow
            {
                Script = record.Script,
                Export = record.Export,
                Kind = record.Kind,
                Key = record.Key,
                Sync = record.Sync,
                Vessel = record.Vessel,
                Clock = record.Clock,
                IntervalSeconds = record.IntervalSeconds,
                Enabled = record.Enabled,
            });
        }

        var body = records.Count == 0 ? "" : TomlSerializer.Serialize(file, TomlOptions);
        AtomicFile.WriteAllText(Path, FileHeader + "\n" + body);
    }

    private sealed class BindingFile
    {
        public List<BindingRow> Binding { get; set; } = [];
    }

    private sealed class BindingRow
    {
        public string Script { get; set; } = "";

        public string Export { get; set; } = "";

        public string? Kind { get; set; } = "event";

        public string? Key { get; set; } = "";

        public bool Sync { get; set; }

        public string? Vessel { get; set; }

        public string? Clock { get; set; } = "ut";

        public double IntervalSeconds { get; set; }

        public bool Enabled { get; set; } = true;
    }
}
