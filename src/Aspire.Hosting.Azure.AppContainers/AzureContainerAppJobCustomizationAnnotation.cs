// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.AppContainers;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an annotation for customizing an Azure Container App Job.
/// </summary>
public sealed class AzureContainerAppJobCustomizationAnnotation(Action<AzureResourceInfrastructure, ContainerAppJob> configure)
    : IResourceAnnotation
{
    /// <summary>
    /// Gets the configuration action for customizing the Azure Container App Job.
    /// </summary>
    public Action<AzureResourceInfrastructure, ContainerAppJob> Configure { get; } = configure ?? throw new ArgumentNullException(nameof(configure));
}
