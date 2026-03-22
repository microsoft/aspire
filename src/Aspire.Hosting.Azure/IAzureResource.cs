// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an Azure resource, as a marker interface for <see cref="IResource"/>'s
/// that can be deployed to an Azure resource group.
/// </summary>
[AspireExport]
public interface IAzureResource : IResource
{
    /// <summary>
    /// Set by the AzureProvisioner to indicate the task that is provisioning the resource.
    /// </summary>
    public TaskCompletionSource? ProvisioningTaskCompletionSource { get; set; }
}
