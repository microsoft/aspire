// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Associates an Azure Files persistent volume with the identity used by its authorized workloads.
/// </summary>
internal sealed class AzureFileSharePersistentVolumeAnnotation(
    AzureFileStorageShareResource share,
    AzureUserAssignedIdentityResource identity) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Azure file share that backs the persistent volume.
    /// </summary>
    public AzureFileStorageShareResource Share { get; } = share;

    /// <summary>
    /// Gets the user-assigned identity used to mount the persistent volume.
    /// </summary>
    public AzureUserAssignedIdentityResource Identity { get; } = identity;
}
