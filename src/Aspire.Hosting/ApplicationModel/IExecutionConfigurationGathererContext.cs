// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Resource configuration gatherer context.
/// </summary>
public interface IExecutionConfigurationGathererContext
{
    /// <summary>
    /// Collection of unprocessed resource command line arguments.
    /// </summary>
    List<object> Arguments { get; }

    /// <summary>
    /// Collection of unprocessed resource environment variables.
    /// </summary>
    Dictionary<string, object> EnvironmentVariables { get; }

    /// <summary>
    /// Adds metadata associated with the resource configuration.
    /// </summary>
    /// <param name="metadata">The metadata to add.</param>
    void AddAdditionalData(IExecutionConfigurationData metadata);
}
