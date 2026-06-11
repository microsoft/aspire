// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Gathers resource configurations (arguments and environment variables) and optionally
/// applies additional metadata to the resource.
/// </summary>
public interface IExecutionConfigurationGatherer
{
    /// <summary>
    /// Gathers the relevant resource execution configuration (arguments, environment variables, and optionally additional custom data)
    /// </summary>
    /// <param name="context">The initial resource configuration context.</param>
    /// <param name="resource">The resource for which configuration is being gathered.</param>
    /// <param name="resourceLogger">The logger for the resource.</param>
    /// <param name="executionContext">The execution context in which the resource is being configured.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    ValueTask GatherAsync(IExecutionConfigurationGathererContext context, IResource resource, ILogger resourceLogger, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken = default);
}
