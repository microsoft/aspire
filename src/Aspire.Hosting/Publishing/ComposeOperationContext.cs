// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Provides the parameters needed to execute a Docker Compose operation against a container runtime.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ComposeOperationContext
{
    /// <summary>
    /// Gets the path to the Docker Compose YAML file.
    /// </summary>
    public required string ComposeFilePath { get; init; }

    /// <summary>
    /// Gets the compose project name used for resource isolation.
    /// </summary>
    public required string ProjectName { get; init; }

    /// <summary>
    /// Gets the optional path to an environment file to pass to the compose operation.
    /// </summary>
    public string? EnvFilePath { get; init; }

    /// <summary>
    /// Gets the working directory for the compose process.
    /// </summary>
    public required string WorkingDirectory { get; init; }
}
