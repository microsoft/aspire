// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Exceptions;

/// <summary>
/// Exception thrown when attempting to create a project in a directory that already contains files from a previous project.
/// </summary>
internal sealed class ProjectAlreadyExistsException : Exception
{
    public ProjectAlreadyExistsException()
        : base("The output folder already contains files from a previous project.")
    {
    }

    public ProjectAlreadyExistsException(string message)
        : base(message)
    {
    }

    public ProjectAlreadyExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
