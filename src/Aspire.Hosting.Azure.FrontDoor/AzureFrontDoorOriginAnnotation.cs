// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an origin to be added to an Azure Front Door resource.
/// </summary>
internal sealed class AzureFrontDoorOriginAnnotation(IResourceWithEndpoints resource) : IResourceAnnotation
{
    /// <summary>
    /// Gets the resource for this origin.
    /// </summary>
    public IResourceWithEndpoints Resource { get; } = resource ?? throw new ArgumentNullException(nameof(resource));
}
