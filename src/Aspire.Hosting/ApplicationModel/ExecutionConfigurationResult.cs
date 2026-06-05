// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents the configuration (arguments and environment variables) to apply to a specific resource.
/// </summary>
internal sealed class ExecutionConfigurationResult : IExecutionConfigurationResult
{
    /// <inheritdoc/>
    public required IEnumerable<object> References { get; init; }

    /// <inheritdoc/>
    public required IEnumerable<(object Unprocessed, string Processed, bool IsSensitive)> ArgumentsWithUnprocessed { get; init; }

    /// <inheritdoc/>
    public IEnumerable<(string Value, bool IsSensitive)> Arguments => ArgumentsWithUnprocessed.Select(arg => (arg.Processed, arg.IsSensitive));

    /// <inheritdoc/>
    public required IEnumerable<KeyValuePair<string, (object Unprocessed, string Processed)>> EnvironmentVariablesWithUnprocessed { get; init; }

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<string, string>> EnvironmentVariables => EnvironmentVariablesWithUnprocessed.Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value.Processed));

    /// <inheritdoc/>
    public required IEnumerable<IExecutionConfigurationData> AdditionalConfigurationData { get; init; }

    /// <inheritdoc/>
    public Exception? Exception { get; init; }
}
