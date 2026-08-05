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
    /// Gets or sets the resource tier.
    /// </summary>
    public AzureSandboxTier Tier { get; set; } = AzureSandboxTier.Medium;

    /// <summary>
    /// Gets or sets a value indicating whether auto-suspend is enabled.
    /// </summary>
    public bool? AutoSuspendEnabled { get; set; }

    /// <summary>
    /// Gets or sets the idle interval before auto-suspend runs.
    /// </summary>
    public TimeSpan? AutoSuspendInterval { get; set; }

    /// <summary>
    /// Gets or sets the sandbox suspend mode.
    /// </summary>
    public AzureSandboxAutoSuspendMode? AutoSuspendMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether auto-delete is enabled.
    /// </summary>
    public bool? AutoDeleteEnabled { get; set; }

    /// <summary>
    /// Gets or sets the interval before auto-delete runs.
    /// </summary>
    public TimeSpan? AutoDeleteInterval { get; set; }

    /// <summary>
    /// Gets or sets the event that starts the auto-delete interval.
    /// </summary>
    public AzureSandboxAutoDeleteTrigger? AutoDeleteTrigger { get; set; }

    /// <summary>
    /// Gets or sets how long to wait for an exposed HTTP endpoint to become ready.
    /// </summary>
    public TimeSpan? PublicEndpointReadyTimeout { get; set; }

    /// <summary>
    /// Gets or sets endpoint-specific sandbox option overrides.
    /// </summary>
    public AzureSandboxEndpointOptions[]? Endpoints { get; set; }
}

/// <summary>
/// Azure Container Apps sandbox resource tiers.
/// </summary>
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum AzureSandboxTier
{
    /// <summary>0.25 vCPU, 0.5 GiB memory, and 20 GiB disk.</summary>
    ExtraSmall,

    /// <summary>0.5 vCPU, 1 GiB memory, and 20 GiB disk.</summary>
    Small,

    /// <summary>1 vCPU, 2 GiB memory, and 20 GiB disk.</summary>
    Medium,

    /// <summary>2 vCPU, 4 GiB memory, and 40 GiB disk.</summary>
    Large,

    /// <summary>4 vCPU, 8 GiB memory, and 80 GiB disk.</summary>
    ExtraLarge
}

/// <summary>
/// Azure Container Apps sandbox auto-suspend modes.
/// </summary>
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum AzureSandboxAutoSuspendMode
{
    /// <summary>Disables snapshot preservation.</summary>
    None,

    /// <summary>Preserves memory and disk state.</summary>
    Memory,

    /// <summary>Preserves disk state only.</summary>
    Disk
}

/// <summary>
/// Events that can start the Azure Container Apps sandbox auto-delete interval.
/// </summary>
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum AzureSandboxAutoDeleteTrigger
{
    /// <summary>Starts the interval after the sandbox is suspended.</summary>
    AfterSuspend,

    /// <summary>Starts the interval after the sandbox is created.</summary>
    AfterCreation
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
