// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an origin group to be added to an Azure Front Door resource: one backend application, plus
/// one origin per regional stamp of that application.
/// </summary>
internal sealed class AzureFrontDoorOriginAnnotation(IResourceWithEndpoints resource, AzureFrontDoorOriginGroupBuilder settings) : IResourceAnnotation
{
    /// <summary>
    /// Gets the resource for this origin group.
    /// </summary>
    public IResourceWithEndpoints Resource { get; } = resource ?? throw new ArgumentNullException(nameof(resource));

    /// <summary>
    /// Gets the origin group configuration, including routing, health probe, and per-stamp overrides.
    /// </summary>
    public AzureFrontDoorOriginGroupBuilder Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));
}
