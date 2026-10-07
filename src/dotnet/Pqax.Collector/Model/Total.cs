namespace Pqax.Collector;

/// <summary>
/// A sum that follows "unknown is not zero": it is null until a term is added, and null for good once a term is
/// unknown. <see cref="Zero"/> starts from a known zero, for a sum over a set that may be empty.
/// </summary>
internal sealed class Total
{
    private long sum;
    private bool any;
    private bool unknown;

    /// <summary>A sum that is 0 before any term.</summary>
    public static Total Zero() => new Total { any = true };

    public long? Value => any && !unknown ? sum : (long?)null;

    public void Add(long? term)
    {
        any = true;
        if (term == null)
        {
            unknown = true;
        }
        else
        {
            sum += term.Value;
        }
    }

    public void MarkUnknown()
    {
        any = true;
        unknown = true;
    }
}
