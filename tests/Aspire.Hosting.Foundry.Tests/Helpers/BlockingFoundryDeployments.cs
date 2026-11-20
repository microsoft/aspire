// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable OPENAI001 // Responses API is experimental

using OpenAI.Responses;

namespace Aspire.Hosting.Foundry.Tests;

internal sealed class BlockingFoundryTool(string name, AzureCognitiveServicesProjectResource project)
    : FoundryToolResource(name, project)
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The blocking tool should only complete through cancellation.");
    }
}

internal sealed class BlockingToolboxAdministration : IFoundryToolboxAdministration
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<FoundryToolboxState?> GetAsync(string name, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The blocking administration call should only complete through cancellation.");
    }

    public Task<string> CreateVersionAsync(FoundryToolboxDeploymentDefinition definition, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unexpected toolbox creation.");

    public Task PromoteVersionAsync(string name, string version, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unexpected toolbox promotion.");
}
