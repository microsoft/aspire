// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// This event is published before the application starts.
/// </summary>
/// <param name="services">The <see cref="IServiceProvider"/> instance.</param>
/// <param name="model">The <see cref="DistributedApplicationModel"/> instance.</param>
/// <remarks>
/// Subscribing to this event is analogous to implementing the <see cref="Aspire.Hosting.Lifecycle.IDistributedApplicationLifecycleHook.BeforeStartAsync(Aspire.Hosting.ApplicationModel.DistributedApplicationModel, CancellationToken)"/>
/// method. This event provides access to the <see cref="IServiceProvider"/> interface to resolve dependencies including
/// <see cref="DistributedApplicationModel"/> service which is passed in as an argument
/// in <see cref="Aspire.Hosting.Lifecycle.IDistributedApplicationLifecycleHook.BeforeStartAsync(Aspire.Hosting.ApplicationModel.DistributedApplicationModel, CancellationToken)"/>.
/// </remarks>
/// <example>
/// Subscribe to the <see cref="BeforeStartEvent"/> event and resolve the distributed application model.
/// <code lang="C#">
/// var builder = DistributedApplication.CreateBuilder(args);
/// builder.Eventing.Subscribe&lt;BeforeStartEvent&gt;(async (@event, cancellationToken) =&gt; {
///   var appModel = @event.ServiceProvider.GetRequiredService&lt;DistributedApplicationModel&gt;();
///   // Mutate the distributed application model.
/// });
/// </code>
/// </example>
[AspireExport(ExposeProperties = true)]
public class BeforeStartEvent(IServiceProvider services, DistributedApplicationModel model) : IDistributedApplicationEvent
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
