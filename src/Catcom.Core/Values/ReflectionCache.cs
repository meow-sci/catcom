using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Catcom.Core.Values;

/// <summary>
///     Shared per-<see cref="Type"/> reflection metadata for <see cref="ScriptValue.FromObject"/> and
///     <see cref="SnapshotPath"/>. Everything is cached in concurrent dictionaries; nothing here throws.
/// </summary>
internal static class ReflectionCache
{
    /// <summary>One readable public instance property and its script-facing (camelCase) key.</summary>
    internal sealed record Accessor(string Key, string NormalizedName, PropertyInfo Property)
    {
        /// <summary>Reads the property; a throwing getter yields <c>null</c> and sets <paramref name="faulted"/>.</summary>
        internal object? Read(object target, out bool faulted)
        {
            try
            {
                faulted = false;
                return Property.GetValue(target);
            }
            catch
            {
                faulted = true;
                return null;
            }
        }
    }

    private static readonly ConcurrentDictionary<Type, Accessor[]> Properties = new();
    private static readonly ConcurrentDictionary<(Type, string), Accessor?> ByNormalizedName = new();
    private static readonly ConcurrentDictionary<Type, bool> StringKeyedReadOnlyDictionary = new();
    private static readonly ConcurrentDictionary<Type, (PropertyInfo Key, PropertyInfo Value)?> PairAccessors = new();

    /// <summary>Public instance readable non-indexer properties, in <c>GetProperties()</c> order.</summary>
    internal static Accessor[] GetAccessors(Type type)
        => Properties.GetOrAdd(type, static t =>
        {
            var list = new List<Accessor>();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length != 0 || p.GetMethod is not { IsPublic: true })
                    continue;
                list.Add(new Accessor(JsonNamingPolicy.CamelCase.ConvertName(p.Name), Normalize(p.Name), p));
            }

            return list.ToArray();
        });

    /// <summary>The first property whose normalized name equals <paramref name="normalizedSegment"/> (misses cached).</summary>
    internal static Accessor? FindByNormalizedName(Type type, string normalizedSegment)
        => ByNormalizedName.GetOrAdd((type, normalizedSegment), static key =>
        {
            foreach (var accessor in GetAccessors(key.Item1))
            {
                if (string.Equals(accessor.NormalizedName, key.Item2, StringComparison.Ordinal))
                    return accessor;
            }

            return null;
        });

    /// <summary>Lowercase-invariant with <c>'_'</c> and <c>'-'</c> removed, so camelCase/PascalCase/snake_case compare equal.</summary>
    internal static string Normalize(string name)
    {
        var buffer = new char[name.Length];
        var n = 0;
        foreach (var c in name)
        {
            if (c is '_' or '-')
                continue;
            buffer[n++] = char.ToLowerInvariant(c);
        }

        return new string(buffer, 0, n);
    }

    /// <summary>
    ///     Enumerates <paramref name="o"/> as string-keyed entries when it is an <see cref="IDictionary"/>
    ///     or an <c>IReadOnlyDictionary&lt;string, T&gt;</c>. Returns <c>false</c> (and no entries) when it is
    ///     neither, or when an <see cref="IDictionary"/> has non-string keys.
    /// </summary>
    internal static bool TryGetStringEntries(object o, out List<KeyValuePair<string, object?>> entries)
    {
        entries = [];
        try
        {
            if (o is IDictionary dictionary)
            {
                var e = dictionary.GetEnumerator();
                while (e.MoveNext())
                {
                    if (e.Key is not string key)
                        return false;
                    entries.Add(new KeyValuePair<string, object?>(key, e.Value));
                }

                return true;
            }

            if (o is IEnumerable enumerable && IsStringKeyedReadOnlyDictionary(o.GetType()))
            {
                foreach (var pair in enumerable)
                {
                    if (pair is null)
                        continue;
                    var accessors = GetPairAccessors(pair.GetType());
                    if (accessors is null || accessors.Value.Key.GetValue(pair) is not string key)
                        return false;
                    entries.Add(new KeyValuePair<string, object?>(key, accessors.Value.Value.GetValue(pair)));
                }

                return true;
            }
        }
        catch
        {
            // A misbehaving collection must not break a read; treat it as "not a dictionary".
        }

        entries = [];
        return false;
    }

    private static bool IsStringKeyedReadOnlyDictionary(Type type)
        => StringKeyedReadOnlyDictionary.GetOrAdd(type, static t =>
        {
            foreach (var i in t.GetInterfaces())
            {
                if (i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
                    && i.GetGenericArguments()[0] == typeof(string))
                    return true;
            }

            return false;
        });

    private static (PropertyInfo Key, PropertyInfo Value)? GetPairAccessors(Type pairType)
        => PairAccessors.GetOrAdd(pairType, static t =>
        {
            var key = t.GetProperty("Key");
            var value = t.GetProperty("Value");
            return key is null || value is null ? null : (key, value);
        });
}
