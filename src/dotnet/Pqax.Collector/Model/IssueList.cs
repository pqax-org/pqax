using System.Collections.Generic;
using System.Globalization;

namespace Pqax.Collector;

/// <summary>
/// The issues of one table. It keeps the first ones, up to a limit, and counts the rest in one last issue, so that a
/// table that fails on every file does not make the summary large.
/// </summary>
internal sealed class IssueList
{
    public const int Limit = 100;

    private readonly List<CollectedIssue> issues = new List<CollectedIssue>();
    private readonly object sync = new object();
    private int dropped;

    public void Add(string code, string message, string? path = null)
    {
        lock (sync)
        {
            if (issues.Count < Limit)
            {
                issues.Add(new CollectedIssue(code, message, path));
            }
            else
            {
                dropped++;
            }
        }
    }

    public bool Has(string code)
    {
        lock (sync)
        {
            return issues.Exists(i => i.Code == code);
        }
    }

    /// <summary>The issues kept, and one more that counts those dropped.</summary>
    public List<CollectedIssue> ToList()
    {
        lock (sync)
        {
            var list = new List<CollectedIssue>(issues);
            if (dropped > 0)
            {
                list.Add(new CollectedIssue(
                    IssueCodes.IssuesTruncated,
                    dropped.ToString(CultureInfo.InvariantCulture) + " more issues of this table were not kept.",
                    null));
            }

            return list;
        }
    }
}
