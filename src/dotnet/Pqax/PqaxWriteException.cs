using System;

namespace Pqax;

/// <summary>
/// <see cref="FootprintBuilder"/> refuses to write a footprint that breaks a rule of the format. <see cref="Code"/> is
/// the code of the rule, as <see cref="PqaxValidator"/> reports it: <c>manifest.required</c> when the collection has no
/// level, <c>table.idUnique</c> for an id that is not positive or that two rows share, <c>table.required</c> for a row
/// without a value in a required field, and <c>json.integerRange</c> for an integer of the format beyond 2^53 - 1 in
/// absolute value.
/// </summary>
/// <remarks>
/// It derives from <see cref="InvalidOperationException"/>, which the builder threw for a repeated id before it had
/// codes. The builder checks the tables and fields of the format; what a producer adds is written as it is.
/// </remarks>
public sealed class PqaxWriteException : InvalidOperationException
{
    /// <summary>Creates the exception with the code of the rule, a message, and where the rule is broken.</summary>
    /// <param name="code">The code of the validation rule that the footprint breaks.</param>
    /// <param name="message">What is wrong.</param>
    /// <param name="table">The table, or null for the collection.</param>
    /// <param name="field">The field, or for the collection <c>collection.level</c> and the like.</param>
    public PqaxWriteException(string code, string message, string? table, string? field)
        : base(message)
    {
        Code = code;
        Table = table;
        Field = field;
    }

    /// <summary>The code of the validation rule that the footprint breaks, such as <c>table.required</c>.</summary>
    public string Code { get; }

    /// <summary>The table of the row that breaks the rule, such as <c>columns</c>. Null for the collection.</summary>
    public string? Table { get; }

    /// <summary>
    /// The field that breaks the rule, such as <c>name</c>; for a member of a nested object, the field that holds the
    /// object, such as <c>fileSizes</c>; for the collection, <c>collection.level</c>, <c>collection.requestCount</c>, or
    /// <c>collection.bytesRead</c>.
    /// </summary>
    public string? Field { get; }
}
