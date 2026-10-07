using System;

namespace Pqax;

/// <summary>
/// The file is not a PQAX file that this library can read. <see cref="Code"/> is the code of the validation rule
/// that the file breaks: <c>container.manifestMissing</c>, <c>manifest.format</c>, <c>manifest.versionUnknown</c>,
/// or <c>json.invalid</c>; <c>manifest.partMissing</c> when a detail part that a read needs is not in the archive;
/// <c>embedded.partMissing</c> when a VPAX file has no part with the name of the footprint; and for an archive that
/// this reader cannot open, <c>container.zip64</c>, <c>container.method</c>, or <c>container.encrypted</c>.
/// </summary>
public sealed class PqaxFormatException : Exception
{
    /// <summary>Creates the exception with the code of the rule and a message.</summary>
    /// <param name="code">The code of the validation rule that the file breaks.</param>
    /// <param name="message">What is wrong.</param>
    public PqaxFormatException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Creates the exception with the code of the rule, a message, and the exception that caused it.</summary>
    /// <param name="code">The code of the validation rule that the file breaks.</param>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">The exception that caused it.</param>
    public PqaxFormatException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>The code of the validation rule that the file breaks, such as <c>manifest.format</c>.</summary>
    public string Code { get; }
}
