using System.Collections;
using System.Globalization;

namespace KsaScripting.Core.Values;

/// <summary>
///     Resolves dotted paths such as <c>position.cci.x</c> or <c>engines.0.active</c> against a snapshot
///     record or a <see cref="ScriptValue"/> tree. This is the host side of <c>Read(handle, path)</c>.
/// </summary>
/// <remarks>
///     Segments are split on <c>'.'</c>. Names match case- and separator-insensitively: the segment and the
///     member name are lowercased and stripped of <c>'_'</c>/<c>'-'</c>, so <c>positionCci</c>,
///     <c>position_cci</c> and <c>PositionCci</c> are the same. Lists and arrays are indexed by a
///     non-negative integer segment. Anything that cannot be resolved yields <see cref="ScriptValue.Null"/>.
/// </remarks>
public static class SnapshotPath
{
    /// <summary>
    ///     Walks <paramref name="path"/> from <paramref name="root"/> and converts the result with
    ///     <see cref="ScriptValue.FromObject"/>. Never throws.
    /// </summary>
    /// <param name="root">A snapshot record, a CLR collection/dictionary, or a <see cref="ScriptValue"/>.</param>
    /// <param name="path">
    ///     A dotted path; <c>null</c>, empty or whitespace selects <paramref name="root"/> itself. An empty
    ///     segment (<c>"a..b"</c>) resolves to Null.
    /// </param>
    /// <returns>The value at the path, or <see cref="ScriptValue.Null"/> when absent.</returns>
    public static ScriptValue Resolve(object? root, string? path)
    {
        try
        {
            if (root is null)
                return ScriptValue.Null;
            if (string.IsNullOrWhiteSpace(path))
                return ScriptValue.FromObject(root);

            object? current = root;
            foreach (var segment in path.Split('.'))
            {
                if (segment.Length == 0 || current is null)
                    return ScriptValue.Null;
                if (!TryStep(current, segment, out current))
                    return ScriptValue.Null;
            }

            return ScriptValue.FromObject(current);
        }
        catch
        {
            return ScriptValue.Null;
        }
    }

    private static bool TryStep(object current, string segment, out object? next)
    {
        next = null;
        switch (current)
        {
            case ScriptValue sv:
                return StepScriptValue(sv, segment, out next);
            case string or bool or char or Enum or Guid or decimal or byte[]:
                return false;
        }

        if (current.GetType().IsPrimitive)
            return false;

        if (ReflectionCache.TryGetStringEntries(current, out var entries))
        {
            foreach (var pair in entries)
            {
                if (string.Equals(pair.Key, segment, StringComparison.Ordinal))
                {
                    next = pair.Value;
                    return true;
                }
            }

            var wanted = ReflectionCache.Normalize(segment);
            foreach (var pair in entries)
            {
                if (string.Equals(ReflectionCache.Normalize(pair.Key), wanted, StringComparison.Ordinal))
                {
                    next = pair.Value;
                    return true;
                }
            }

            return false;
        }

        if (current is IList list)
        {
            if (!TryParseIndex(segment, out var index) || index >= list.Count)
                return false;
            next = list[index];
            return true;
        }

        if (current is IEnumerable enumerable)
        {
            if (!TryParseIndex(segment, out var index))
                return false;
            var i = 0;
            foreach (var item in enumerable)
            {
                if (i++ == index)
                {
                    next = item;
                    return true;
                }
            }

            return false;
        }

        var accessor = ReflectionCache.FindByNormalizedName(current.GetType(), ReflectionCache.Normalize(segment));
        if (accessor is null)
            return false;

        next = accessor.Read(current, out var faulted);
        return !faulted;
    }

    private static bool StepScriptValue(ScriptValue value, string segment, out object? next)
    {
        next = null;
        switch (value.Kind)
        {
            case ScriptValueKind.Map:
            {
                var map = value.AsMap();
                if (map.TryGetValue(segment, out var exact))
                {
                    next = exact;
                    return true;
                }

                var wanted = ReflectionCache.Normalize(segment);
                foreach (var pair in map)
                {
                    if (string.Equals(ReflectionCache.Normalize(pair.Key), wanted, StringComparison.Ordinal))
                    {
                        next = pair.Value;
                        return true;
                    }
                }

                return false;
            }

            case ScriptValueKind.List:
            {
                var list = value.AsList();
                if (!TryParseIndex(segment, out var index) || index >= list.Count)
                    return false;
                next = list[index];
                return true;
            }

            default:
                return false;
        }
    }

    private static bool TryParseIndex(string segment, out int index)
        => int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0;
}
