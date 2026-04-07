// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// The <see cref="ConnectionStringAvailableEvent"/> is raised when a connection string becomes available for a resource.
/// </summary>
/// <param name="resource">The <see cref="IResource"/> for the event.</param>
/// <param name="services">The <see cref="IServiceProvider"/> for the app host.</param>
[AspireExport(ExposeProperties = true)]
public class ConnectionStringAvailableEvent(IResource resource, IServiceProvider services) : IDistributedApplicationResourceEvent
{
    /// <inheritdoc />
    public IResource Resource => resource;

    /// <inheritdoc />
    public IServiceProvider Services => services;
}
