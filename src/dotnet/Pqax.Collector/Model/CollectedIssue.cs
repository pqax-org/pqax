namespace Pqax.Collector;

/// <summary>An issue of a table, before it becomes a row of <c>issues</c>.</summary>
internal sealed class CollectedIssue
{
    public CollectedIssue(string code, string message, string? path)
    {
        Code = code;
        Message = message;
        Path = path;
    }

    public string Code { get; }

    public string Message { get; }

    /// <summary>The file concerned, relative to the table, with partition values replaced unless the collection includes them.</summary>
    public string? Path { get; }
}
