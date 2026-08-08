// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Utils;

public static class LaunchConfigurationTestHelpers
{
    public static LaunchConfigurationCallbackContext CreateCallbackContext(
        IResource resource,
        string mode = ExecutableLaunchMode.Debug,
        IExecutionConfigurationResult? executionConfiguration = null,
        DistributedApplicationExecutionContext? executionContext = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        executionConfiguration ??= CreateExecutionConfigurationResult();
        return new LaunchConfigurationCallbackContext(
            mode,
            resource,
            executionConfiguration,
            executionConfiguration,
            executionContext ?? new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            logger ?? NullLogger.Instance,
            cancellationToken);
    }

    public static IExecutionConfigurationResult CreateExecutionConfigurationResult(
        IEnumerable<string>? arguments = null,
        IEnumerable<KeyValuePair<string, string>>? environmentVariables = null,
        Exception? exception = null)
    {
        return new ExecutionConfigurationResult
        {
            References = [],
            ArgumentsWithUnprocessed = (arguments ?? [])
                .Select(value => ((object)value, value, false))
                .ToArray(),
            EnvironmentVariablesWithUnprocessed = (environmentVariables ?? [])
                .Select(pair => new KeyValuePair<string, (object Unprocessed, string Processed)>(
                    pair.Key,
                    (pair.Value, pair.Value)))
                .ToArray(),
            AdditionalConfigurationData = [],
            Exception = exception
        };
    }
}
