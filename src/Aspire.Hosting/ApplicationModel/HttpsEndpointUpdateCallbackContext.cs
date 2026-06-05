// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Context provided to the callback of <see cref="ResourceBuilderExtensions.SubscribeHttpsEndpointsUpdate{TResource}"/>
/// when an HTTPS certificate is determined to be available for the resource.
/// </summary>
[Experimental("ASPIRECERTIFICATES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class HttpsEndpointUpdateCallbackContext
{
    /// <summary>
    /// Gets the <see cref="IServiceProvider"/> instance from the application.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// Gets the <see cref="IResource"/> that is being configured for HTTPS.
    /// </summary>
    public required IResource Resource { get; init; }

    /// <summary>
    /// Gets the <see cref="DistributedApplicationModel"/> instance.
    /// </summary>
    public required DistributedApplicationModel Model { get; init; }

    /// <summary>
    /// Gets the <see cref="CancellationToken"/> for the operation.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }
}
