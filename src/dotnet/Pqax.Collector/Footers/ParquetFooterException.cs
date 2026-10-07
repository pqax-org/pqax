using System;

namespace Pqax.Collector;

/// <summary>A Parquet footer that cannot be read. <see cref="Code"/> is the issue code that the collector records.</summary>
public sealed class ParquetFooterException : Exception
{
    /// <summary>Creates an exception.</summary>
    /// <param name="code">The issue code: <see cref="IssueCodes.FooterEncrypted"/>, <see cref="IssueCodes.FooterInvalid"/>, or <see cref="IssueCodes.FooterTooLarge"/>.</param>
    /// <param name="message">What is wrong.</param>
    public ParquetFooterException(string code, string message)
        : base(message)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
    }

    /// <summary>Creates an exception with the exception that caused it.</summary>
    /// <param name="code">The issue code.</param>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ParquetFooterException(string code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
    }

    /// <summary>The issue code.</summary>
    public string Code { get; }
}
