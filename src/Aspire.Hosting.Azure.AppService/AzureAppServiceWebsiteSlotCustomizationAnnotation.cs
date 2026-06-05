// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.AppService;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an annotation for customizing an Azure Web App slot.
/// </summary>
/// <param name="configure">The configuration action for customizing the Azure Web App slot.</param>
public sealed class AzureAppServiceWebsiteSlotCustomizationAnnotation(Action<AzureResourceInfrastructure, WebSiteSlot> configure)
    : IResourceAnnotation
{
    /// <summary>
    /// Gets the configuration action for customizing the Azure Web App slot.
    /// </summary>
    public Action<AzureResourceInfrastructure, WebSiteSlot> Configure { get; } = configure ?? throw new ArgumentNullException(nameof(configure));
}
