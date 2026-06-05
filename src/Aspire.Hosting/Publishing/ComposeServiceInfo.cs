// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Represents a running service discovered from a compose environment.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ComposeServiceInfo
{
    /// <summary>
    /// Gets the name of the compose service.
    /// </summary>
    public string? Service { get; init; }

    /// <summary>
    /// Gets the published port mappings for the service.
    /// </summary>
    public IReadOnlyList<ComposeServicePort>? Publishers { get; init; }
}

/// <summary>
/// Represents a port mapping for a compose service.
/// </summary>
[Experimental("ASPIRECONTAINERRUNTIME001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ComposeServicePort
{
    /// <summary>
    /// Gets the port published on the host.
    /// </summary>
    public int? PublishedPort { get; init; }

    /// <summary>
    /// Gets the target port inside the container.
    /// </summary>
    public int? TargetPort { get; init; }
}
