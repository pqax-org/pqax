using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Pqax.Collector;

/// <summary>
/// The encodings of a column chunk sorted by name, as a footprint writes them. The parser shares equal lists, so the
/// sorted copy is shared too: a million column chunks hold a handful of lists.
/// </summary>
internal static class SortedEncodings
{
    private static readonly ConditionalWeakTable<IReadOnlyList<string>, IReadOnlyList<string>> Sorted =
        new ConditionalWeakTable<IReadOnlyList<string>, IReadOnlyList<string>>();

    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> ByText =
        new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public static IReadOnlyList<string> Of(IReadOnlyList<string> encodings) => Sorted.GetValue(encodings, Sort);

    private static IReadOnlyList<string> Sort(IReadOnlyList<string> encodings)
    {
        var copy = new List<string>(encodings);
        copy.Sort(StringComparer.Ordinal);
        string text = string.Join(",", copy);
        return ByText.GetOrAdd(text, _ => copy.ToArray());
    }
}
