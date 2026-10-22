namespace Pqax.Cli;

/// <summary>A sign-in that cannot start, such as a variable that is not set. The message never holds a secret.</summary>
public sealed class SignInException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What is wrong, without a secret.</param>
    public SignInException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and its cause.</summary>
    /// <param name="message">What is wrong, without a secret.</param>
    /// <param name="innerException">The cause.</param>
    public SignInException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
