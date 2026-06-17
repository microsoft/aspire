// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures Azure sandbox runtime options for a compute resource.
/// </summary>
[AspireDto]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureSandboxOptions
{
    /// <summary>
    /// Gets or sets the CPU quantity, such as <c>1000m</c>.
    /// </summary>
    public string? Cpu { get; set; }

    /// <summary>
    /// Gets or sets the memory quantity, such as <c>2048Mi</c>.
    /// </summary>
    public string? Memory { get; set; }

    /// <summary>
    /// Gets or sets the disk quantity, such as <c>20480Mi</c>.
    /// </summary>
    public string? Disk { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether auto-suspend is enabled.
    /// </summary>
    public bool? AutoSuspendEnabled { get; set; }

    /// <summary>
    /// Gets or sets the idle interval, in seconds, before auto-suspend runs.
    /// </summary>
    public int? AutoSuspendInterval { get; set; }

    /// <summary>
    /// Gets or sets the sandbox suspend mode. Supported values are <c>Memory</c>, <c>Disk</c>, and <c>None</c>.
    /// </summary>
    public string? AutoSuspendMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether auto-delete is enabled.
    /// </summary>
    public bool? AutoDeleteEnabled { get; set; }

    /// <summary>
    /// Gets or sets the delete interval, in days.
    /// </summary>
    public int? AutoDeleteIntervalInDays { get; set; }

    /// <summary>
    /// Gets or sets the delete interval, in seconds.
    /// </summary>
    public long? AutoDeleteIntervalInSeconds { get; set; }

    /// <summary>
    /// Gets or sets the auto-delete trigger. Supported values are <c>AfterSuspend</c> and <c>AfterCreation</c>.
    /// </summary>
    public string? AutoDeleteTrigger { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether ADC registers the sandbox with the egress proxy.
    /// </summary>
    public bool? EgressProxyEnabled { get; set; }

    /// <summary>
    /// Gets or sets the ADC egress TLS inspection mode. Supported values are <c>Legacy</c>, <c>Partial</c>, <c>Full</c>, and <c>None</c>.
    /// </summary>
    public string? EgressTrafficInspection { get; set; }

    /// <summary>
    /// Gets or sets the number of seconds to wait for an exposed HTTP endpoint to become ready.
    /// </summary>
    public int? PublicEndpointReadyTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets endpoint-specific sandbox option overrides.
    /// </summary>
    public AzureSandboxEndpointOptions[]? Endpoints { get; set; }
}

/// <summary>
/// Overrides Azure sandbox options for a compute resource endpoint.
/// </summary>
[AspireDto]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureSandboxEndpointOptions
{
    /// <summary>
    /// Gets or sets the Aspire endpoint name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sandbox port allows anonymous access.
    /// </summary>
    public bool? Anonymous { get; set; }
}

/// <summary>
/// Captures Azure sandbox-specific runtime options on the compute resource being deployed.
/// </summary>
internal sealed class AzureSandboxContainerOptionsAnnotation(AzureSandboxOptions options) : IResourceAnnotation
{
    public AzureSandboxOptions Options { get; } = options;
}
