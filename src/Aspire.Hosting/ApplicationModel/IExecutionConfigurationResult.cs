// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Configuration (arguments and environment variables) to apply to a specific resource.
/// </summary>
public interface IExecutionConfigurationResult
{
    /// <summary>
    /// Gets the set of references such as <see cref="IValueProvider"/> or <see cref="IManifestExpressionProvider"/> that were used to produce this configuration.
    /// </summary>
    IEnumerable<object> References { get; }

    /// <summary>
    /// Gets the arguments for the resource with the orgiginal unprocessed values included.
    /// </summary>
    IEnumerable<(object Unprocessed, string Processed, bool IsSensitive)> ArgumentsWithUnprocessed { get; }

    /// <summary>
    /// Gets the processed arguments to apply to the resource.
    /// </summary>
    IEnumerable<(string Value, bool IsSensitive)> Arguments { get; }

    /// <summary>
    /// Gets the environment variables to apply to the resource with the original unprocessed values included.
    /// </summary>
    IEnumerable<KeyValuePair<string, (object Unprocessed, string Processed)>> EnvironmentVariablesWithUnprocessed { get; }

    /// <summary>
    /// Gets the processed environment variables to apply to the resource.
    /// </summary>
    IEnumerable<KeyValuePair<string, string>> EnvironmentVariables { get; }

    /// <summary>
    /// Gets additional configuration data associated with the resource configuration.
    /// </summary>
    IEnumerable<IExecutionConfigurationData> AdditionalConfigurationData { get; }

    /// <summary>
    /// Gets any exception that occurred while building the configuration.
    /// </summary>
    Exception? Exception { get; }
}
