// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Builder for gathering and resolving the execution configuration (arguments and environment variables) for a specific resource.
/// </summary>
public interface IExecutionConfigurationBuilder
{
    /// <summary>
    /// Adds a configuration gatherer to the builder.
    /// </summary>
    /// <param name="gatherer">The configuration gatherer to add.</param>
    /// <returns>The current instance of the builder.</returns>
    IExecutionConfigurationBuilder AddExecutionConfigurationGatherer(IExecutionConfigurationGatherer gatherer);

    /// <summary>
    /// Builds the processed resource configuration (resolved arguments and environment variables).
    /// </summary>
    /// <param name="executionContext">The distributed application execution context.</param>
    /// <param name="resourceLogger">A logger instance for the resource. If none is provided, a default logger will be used.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The resource configuration result. Any exceptions that occurred while processing are available via the <see cref="IExecutionConfigurationResult.Exception"/> property.</returns>
    Task<IExecutionConfigurationResult> BuildAsync(DistributedApplicationExecutionContext executionContext, ILogger? resourceLogger = null, CancellationToken cancellationToken = default);
}
