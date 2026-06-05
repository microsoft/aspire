// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an annotation for the creation of an Azure App Service website and its deployment slot, including
/// configuration customization.
/// </summary>
internal sealed class AzureAppServiceWebsiteRefreshProvisionableResourceAnnotation
    : IResourceAnnotation
{
}
