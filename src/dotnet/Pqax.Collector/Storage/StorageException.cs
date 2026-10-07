using System;

namespace Pqax.Collector;

/// <summary>
/// A storage request that failed. The message never contains a path, so that it can be shown or logged without
/// revealing names; the collector records the path apart, in the issue.
/// </summary>
public sealed class StorageException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What failed, without a path.</param>
    public StorageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and the HTTP status of the failed request.</summary>
    /// <param name="message">What failed, without a path.</param>
    /// <param name="statusCode">The HTTP status code, or the code that a local storage maps its error to: 404 for a missing file, 403 for denied access.</param>
    public StorageException(string message, int? statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>Creates an exception with a message, a status, and the exception that caused it.</summary>
    /// <param name="message">What failed, without a path.</param>
    /// <param name="statusCode">The HTTP status code, when there is one.</param>
    /// <param name="innerException">The cause.</param>
    public StorageException(string message, int? statusCode, Exception? innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status code of the failed request, or the code that a local storage maps its error to. Null when there is none.</summary>
    public int? StatusCode { get; }

    /// <summary>Whether the file or folder does not exist.</summary>
    public bool IsNotFound => StatusCode == 404;

    /// <summary>Whether access was denied.</summary>
    public bool IsAccessDenied => StatusCode == 401 || StatusCode == 403;
}
