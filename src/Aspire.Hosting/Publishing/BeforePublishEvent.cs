// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// This event is published before the distributed application is published.
/// </summary>
/// <param name="services">The <see cref="IServiceProvider"/> for the app host.</param>
/// <param name="model">The <see cref="DistributedApplicationModel"/>.</param>
[AspireExport(ExposeProperties = true)]
public sealed class BeforePublishEvent(IServiceProvider services, DistributedApplicationModel model) : IDistributedApplicationEvent
{
    /// <summary>
    /// The <see cref="IServiceProvider"/> for the app host.
    /// </summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>
    /// The <see cref="DistributedApplicationModel"/> instance.
    /// </summary>
    public DistributedApplicationModel Model { get; } = model;
}
