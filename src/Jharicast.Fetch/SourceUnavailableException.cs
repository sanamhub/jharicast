using System;

namespace Jharicast.Fetch;

/// <summary>
/// A request was refused before it left the process: the host is disabled by configuration, or
/// its circuit breaker is open after repeated failures (ADR-0007). Callers report the source as
/// failing or disabled; they must not retry around it.
/// </summary>
public sealed class SourceUnavailableException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public SourceUnavailableException()
        : base("The source is unavailable.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the source is unavailable, naming the host.</param>
    public SourceUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the failure that caused it.</summary>
    /// <param name="message">Why the source is unavailable, naming the host.</param>
    /// <param name="innerException">The cause.</param>
    public SourceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
