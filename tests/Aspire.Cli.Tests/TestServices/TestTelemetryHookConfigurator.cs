// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestTelemetryHookConfigurator : ITelemetryHookConfigurator
{
    public ConcurrentQueue<AgentInitRequest> Requests { get; } = new();

    public Func<AgentInitRequest, IEnumerable<AgentConfigurationTarget>>? PlanCallback { get; set; }

    public IEnumerable<AgentConfigurationTarget> Plan(AgentInitRequest request)
    {
        Requests.Enqueue(request);
        return PlanCallback?.Invoke(request) ?? [];
    }
}
