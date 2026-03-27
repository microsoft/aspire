// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// This event is published after all resources have been created.
/// </summary>
/// <param name="services">The <see cref="IServiceProvider"/> instance.</param>
/// <param name="model">The <see cref="DistributedApplicationModel"/> instance.</param>
/// <remarks>
/// Subscribing to this event is analogous to implementing the <see cref="Aspire.Hosting.Lifecycle.IDistributedApplicationLifecycleHook.AfterResourcesCreatedAsync(DistributedApplicationModel, CancellationToken)"/>
/// method. This event provides access to the <see cref="IServiceProvider"/> interface to resolve dependencies including
/// <see cref="DistributedApplicationModel"/> service which is passed in as an argument
/// in <see cref="Aspire.Hosting.Lifecycle.IDistributedApplicationLifecycleHook.AfterResourcesCreatedAsync(Aspire.Hosting.ApplicationModel.DistributedApplicationModel, CancellationToken)"/>.
/// </remarks>
/// <example>
/// Subscribe to the <see cref="AfterResourcesCreatedEvent"/> event and resolve the distributed application model.
/// <code lang="C#">
/// var builder = DistributedApplication.CreateBuilder(args);
/// builder.Eventing.Subscribe&lt;AfterResourcesCreatedEvent&gt;(async (@event, cancellationToken) =&gt; {
///   var appModel = @event.ServiceProvider.GetRequiredService&lt;DistributedApplicationModel&gt;();
///   // Run post startup logic.
/// });
/// </code>
/// </example>
[AspireExport(ExposeProperties = true)]
public class AfterResourcesCreatedEvent(IServiceProvider services, DistributedApplicationModel model) : IDistributedApplicationEvent
{
    /// <summary>
    /// The <see cref="IServiceProvider"/> instance.
    /// </summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>
    /// The <see cref="DistributedApplicationModel"/> instance.
    /// </summary>
    public DistributedApplicationModel Model { get; } = model;
}
