// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Exception thrown when pipeline step scheduling onto workflow jobs is invalid.
/// </summary>
public class SchedulingValidationException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SchedulingValidationException"/> class.
    /// </summary>
    /// <param name="message">The error message describing the scheduling violation.</param>
    public SchedulingValidationException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SchedulingValidationException"/> class.
    /// </summary>
    /// <param name="message">The error message describing the scheduling violation.</param>
    /// <param name="innerException">The inner exception.</param>
    public SchedulingValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
