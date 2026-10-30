// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestTelemetryHookConfigurator : ITelemetryHookConfigurator
{
    public Exception? Exception { get; init; }
    public AgentEnvironment ConfiguredEnvironments { get; private set; }

    public Task<TelemetryHookConfigurationResult> ConfigureAsync(
        AgentEnvironment detectedEnvironments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Exception is { } exception)
        {
            throw exception;
        }

        ConfiguredEnvironments = detectedEnvironments;
        return Task.FromResult(new TelemetryHookConfigurationResult(ConfiguredEnvironments, []));
    }
}
