// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a compute resource deployed into an Azure Container Apps sandbox from a container image.
/// </summary>
[AspireExport(ExposeProperties = true)]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureSandboxContainerResource : Resource, IResourceWithParent<AzureSandboxGroupResource>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureSandboxContainerResource"/> class.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="targetResource">The compute resource that produces or references the container image.</param>
    /// <param name="parent">The sandbox group that hosts the sandbox.</param>
    /// <param name="autoSuspend">A value indicating whether the sandbox can auto-suspend when idle.</param>
    public AzureSandboxContainerResource(
        string name,
        IResource targetResource,
        AzureSandboxGroupResource parent,
        bool autoSuspend)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(targetResource);
        ArgumentNullException.ThrowIfNull(parent);

        TargetResource = targetResource;
        Parent = parent;
        AutoSuspend = autoSuspend;
    }

    /// <summary>
    /// Gets the compute resource that produces or references the container image.
    /// </summary>
    public IResource TargetResource { get; }

    /// <inheritdoc/>
    public AzureSandboxGroupResource Parent { get; }

    /// <summary>
    /// Gets a value indicating whether the sandbox can auto-suspend when idle.
    /// </summary>
    public bool AutoSuspend { get; }
}
