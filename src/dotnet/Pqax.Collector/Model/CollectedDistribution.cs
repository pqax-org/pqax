using System.Collections.Generic;

namespace Pqax.Collector;

/// <summary>The shape of a set of values: count, sum, extremes, and quartiles taken by position in the sorted values.</summary>
internal sealed class CollectedDistribution
{
    private CollectedDistribution(long count, long sum, long minimum, long firstQuartile, long median, long thirdQuartile, long maximum)
    {
        Count = count;
        Sum = sum;
        Minimum = minimum;
        FirstQuartile = firstQuartile;
        Median = median;
        ThirdQuartile = thirdQuartile;
        Maximum = maximum;
    }

    public long Count { get; }

    public long Sum { get; }

    public long Minimum { get; }

    public long FirstQuartile { get; }

    public long Median { get; }

    public long ThirdQuartile { get; }

    public long Maximum { get; }

    /// <summary>The distribution of the values, as the prototype computes it: sorted[n/4], sorted[n/2], sorted[3n/4]. Null for no value.</summary>
    public static CollectedDistribution? Of(List<long> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = new List<long>(values);
        sorted.Sort();
        int n = sorted.Count;
        long sum = 0;
        foreach (long value in sorted)
        {
            sum += value;
        }

        return new CollectedDistribution(n, sum, sorted[0], sorted[n / 4], sorted[n / 2], sorted[(int)(3L * n / 4)], sorted[n - 1]);
    }
}
