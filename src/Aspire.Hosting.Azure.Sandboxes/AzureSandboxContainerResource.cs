// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

internal sealed class AzureSandboxContainerResource : Resource, IResourceWithParent<AzureSandboxGroupResource>
{
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

    public IResource TargetResource { get; }

    public AzureSandboxGroupResource Parent { get; }

    public bool AutoSuspend { get; }
}
