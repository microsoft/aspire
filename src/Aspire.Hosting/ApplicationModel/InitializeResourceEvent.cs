// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// This event is raised by orchestrators to signal to resources that they should initialize themselves.
/// </summary>
/// <param name="resource">The resource that is being created.</param>
/// <param name="distributedApplicationEventing">The <see cref="IDistributedApplicationEventing"/> service for the app host.</param>
/// <param name="resourceLoggerService">The <see cref="ResourceLoggerService"/> for the app host.</param>
/// <param name="resourceNotificationService">The <see cref="ResourceNotificationService"/> for the app host.</param>
/// <param name="services">The <see cref="IServiceProvider"/> for the app host.</param>
/// <remarks>
/// Custom resources can subscribe to this event to perform initialization tasks, including starting background tasks
/// that manage the resource's lifecycle.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class InitializeResourceEvent(
    IResource resource,
    IDistributedApplicationEventing distributedApplicationEventing,
    ResourceLoggerService resourceLoggerService,
    ResourceNotificationService resourceNotificationService,
    IServiceProvider services) : IDistributedApplicationResourceEvent
{
    /// <inheritdoc />
    public IResource Resource { get; } = resource;

    /// <summary>
    /// The <see cref="IDistributedApplicationEventing"/> service for the app host.
    /// </summary>
    public IDistributedApplicationEventing Eventing { get; } = distributedApplicationEventing;

    /// <summary>
    /// An instance of <see cref="ILogger"/> that can be used to log messages for the resource.
    /// </summary>
    public ILogger Logger { get; } = resourceLoggerService.GetLogger(resource);

    /// <summary>
    /// The <see cref="ResourceNotificationService"/> for the app host.
    /// </summary>
    public ResourceNotificationService Notifications { get; } = resourceNotificationService;

    /// <summary>
    /// The <see cref="IServiceProvider"/> for the app host.
    /// </summary>
    public IServiceProvider Services { get; } = services;
}
