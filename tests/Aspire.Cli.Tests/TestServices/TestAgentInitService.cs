// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAgentInitService : IAgentInitService
{
    public ConcurrentQueue<AgentInitRequest> Requests { get; } = new();

    public AgentInitResult Result { get; set; } = new([]);

    public Func<AgentInitRequest, CancellationToken, Task<AgentInitResult>>? ConfigureAsyncCallback { get; set; }

    public Task<AgentInitResult> ConfigureAsync(AgentInitRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Enqueue(request);

        return ConfigureAsyncCallback?.Invoke(request, cancellationToken) ?? Task.FromResult(Result);
    }
}
