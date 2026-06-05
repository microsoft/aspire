// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Eventing;

/// <summary>
/// Represents an event that is published during the lifecycle of the AppHost.
/// </summary>
[AspireExport]
public interface IDistributedApplicationEvent
{
}

/// <summary>
/// Represents an event that is published during the lifecycle of the AppHost for a specific resource.
/// </summary>
[AspireExport]
public interface IDistributedApplicationResourceEvent : IDistributedApplicationEvent
{
    /// <summary>
    /// Resource associated with this event.
    /// </summary>
    IResource Resource { get; }

    /// <summary>
    /// The <see cref="IServiceProvider"/> instance.
    /// </summary>
    IServiceProvider Services => throw new NotImplementedException();

    /// <summary>
    /// An instance of <see cref="ILogger"/> that can be used to log messages for the resource.
    /// </summary>
    ILogger Logger => Services.GetRequiredService<ResourceLoggerService>().GetLogger(Resource);
}
