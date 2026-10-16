using System.Text.Json;
using KsaScripting.Core.Config;
using KsaScripting.Core.Logging;
using Tomlyn;

namespace KsaScripting.GameMod.Terminals;

/// <summary>The <see cref="TerminalPlacement.Kind"/> values.</summary>
internal static class PlacementKinds
{
    /// <summary>A 2D purrTTY window.</summary>
    public const string Window = "window";

    /// <summary>A terminal quad on a vehicle part.</summary>
    public const string InWorld = "inworld";
}

/// <summary>
///     Persists terminal placements in <c>terminals.toml</c> as an array of <c>[[placement]]</c> tables
///     (<c>script</c>, <c>kind</c>, <c>cols</c>, <c>rows</c>, <c>title</c>, <c>theme</c>, <c>vehicle_id</c>,
///     <c>part_instance_id</c>, <c>offset_x/y/z</c>, <c>rotation_x/y/z</c>, <c>width_meters</c>,
///     <c>height_meters</c>, <c>background_opacity</c>; unset members are omitted). Reads never throw
///     (a malformed file is logged and treated as empty, left untouched); writes are atomic.
/// </summary>
internal sealed class PlacementStore
{
    private const string FileHeader =
        """
        # ksa-scripting terminal placements.
        # Each [[placement]] is re-created when its script starts: kind = "window" opens a purrTTY window,
        # kind = "inworld" puts a terminal quad on a vehicle part (part_instance_id is only valid for the
        # game session it was recorded in).

        """;

    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _path;

    /// <summary>Creates a store over a file; the file need not exist yet.</summary>
    /// <param name="path">The <c>terminals.toml</c> path.</param>
    public PlacementStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    /// <summary>Reads the stored placements in file order; unusable rows are skipped with a warning.</summary>
    /// <returns>The placements.</returns>
    public List<TerminalPlacement> Load()
    {
        string text;
        try
        {
            if (!File.Exists(_path))
                return [];
            text = File.ReadAllText(_path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read '{_path}': {ex.Message}");
            return [];
        }

        PlacementFile? file;
        try
        {
            file = string.IsNullOrWhiteSpace(text) ? null : TomlSerializer.Deserialize<PlacementFile>(text, TomlOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"'{_path}' is not valid ({ex.Message}); ignoring its terminal placements. Fix or delete the file.");
            return [];
        }

        var placements = new List<TerminalPlacement>();
        foreach (var row in file?.Placement ?? [])
        {
            var placement = ToPlacement(row);
            if (placement is not null && !placements.Contains(placement))
                placements.Add(placement);
        }

        return placements;
    }

    /// <summary>Writes the placements atomically, replacing the file.</summary>
    /// <param name="placements">The placements to store.</param>
    public void Save(IReadOnlyList<TerminalPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);

        var file = new PlacementFile();
        foreach (var placement in placements)
            file.Placement.Add(ToRow(placement));

        var body = placements.Count == 0 ? "" : TomlSerializer.Serialize(file, TomlOptions);
        AtomicFile.WriteAllText(_path, FileHeader + "\n" + body);
    }

    private TerminalPlacement? ToPlacement(PlacementRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Script))
        {
            Log.Warn($"Ignoring a placement in '{_path}' without a script.");
            return null;
        }

        var cols = Math.Clamp(row.Cols, 1, 1000);
        var rows = Math.Clamp(row.Rows, 1, 1000);
        var theme = string.IsNullOrWhiteSpace(row.Theme) ? null : row.Theme;

        if (string.Equals(row.Kind, PlacementKinds.Window, StringComparison.OrdinalIgnoreCase))
        {
            var title = string.IsNullOrWhiteSpace(row.Title) ? null : row.Title;
            return new TerminalPlacement(row.Script, PlacementKinds.Window, cols, rows, title, theme, null, null, null);
        }

        if (string.Equals(row.Kind, PlacementKinds.InWorld, StringComparison.OrdinalIgnoreCase))
        {
            if (row.PartInstanceId is not long instance || instance < 0 || instance > uint.MaxValue)
            {
                Log.Warn($"Ignoring the in-world placement of '{row.Script}' in '{_path}': part_instance_id is missing.");
                return null;
            }

            var defaults = new InWorldPlacementOptions();
            var options = new InWorldPlacementOptions(
                cols,
                rows,
                (float)(row.OffsetX ?? defaults.OffsetX),
                (float)(row.OffsetY ?? defaults.OffsetY),
                (float)(row.OffsetZ ?? defaults.OffsetZ),
                (float)(row.RotationX ?? defaults.RotationX),
                (float)(row.RotationY ?? defaults.RotationY),
                (float)(row.RotationZ ?? defaults.RotationZ),
                (float)(row.WidthMeters ?? defaults.WidthMeters),
                (float)(row.HeightMeters ?? defaults.HeightMeters),
                theme,
                row.BackgroundOpacity is { } opacity ? (float)opacity : null);
            return new TerminalPlacement(
                row.Script, PlacementKinds.InWorld, cols, rows, null, theme, row.VehicleId ?? "", (uint)instance, options);
        }

        Log.Warn($"Ignoring a placement of '{row.Script}' in '{_path}': unknown kind '{row.Kind}'.");
        return null;
    }

    private static PlacementRow ToRow(TerminalPlacement placement)
    {
        var row = new PlacementRow
        {
            Script = placement.Script,
            Kind = placement.Kind,
            Cols = placement.Cols,
            Rows = placement.Rows,
            Title = placement.Title,
            Theme = placement.Theme,
        };

        if (placement.InWorld is { } world)
        {
            row.VehicleId = placement.VehicleId ?? "";
            row.PartInstanceId = placement.PartInstanceId;
            row.OffsetX = world.OffsetX;
            row.OffsetY = world.OffsetY;
            row.OffsetZ = world.OffsetZ;
            row.RotationX = world.RotationX;
            row.RotationY = world.RotationY;
            row.RotationZ = world.RotationZ;
            row.WidthMeters = world.WidthMeters;
            row.HeightMeters = world.HeightMeters;
            row.BackgroundOpacity = world.BackgroundOpacity;
        }

        return row;
    }

    private sealed class PlacementFile
    {
        public List<PlacementRow> Placement { get; set; } = [];
    }

    private sealed class PlacementRow
    {
        public string Script { get; set; } = "";

        public string? Kind { get; set; } = PlacementKinds.Window;

        public int Cols { get; set; } = 80;

        public int Rows { get; set; } = 24;

        public string? Title { get; set; }

        public string? Theme { get; set; }

        public string? VehicleId { get; set; }

        public long? PartInstanceId { get; set; }

        public double? OffsetX { get; set; }

        public double? OffsetY { get; set; }

        public double? OffsetZ { get; set; }

        public double? RotationX { get; set; }

        public double? RotationY { get; set; }

        public double? RotationZ { get; set; }

        public double? WidthMeters { get; set; }

        public double? HeightMeters { get; set; }

        public double? BackgroundOpacity { get; set; }
    }
}
