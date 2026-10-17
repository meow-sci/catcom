namespace Catcom.Runtime.V8;

/// <summary>One decoded source-map segment. All indices are 0-based; absent fields are <c>-1</c>.</summary>
/// <param name="GeneratedColumn">The 0-based column in the generated line.</param>
/// <param name="SourceIndex">The index into <c>sources</c>, or <c>-1</c> for a 1-field (unmapped) segment.</param>
/// <param name="OriginalLine">The 0-based original line, or <c>-1</c>.</param>
/// <param name="OriginalColumn">The 0-based original column, or <c>-1</c>.</param>
/// <param name="NameIndex">The index into <c>names</c>, or <c>-1</c>.</param>
internal readonly record struct SourceMapSegment(int GeneratedColumn, int SourceIndex, int OriginalLine, int OriginalColumn, int NameIndex)
{
    /// <summary>Whether the segment maps to an original position (4 or 5 fields).</summary>
    public bool HasSource => SourceIndex >= 0;
}
