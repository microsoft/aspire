// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A built-in Foundry tool that enables an agent to write and run Python code
/// in a sandboxed environment for data analysis, math, and chart generation.
/// </summary>
/// <remarks>
/// This tool requires no Azure provisioning or project connections.
/// It is automatically available in all Foundry projects.
/// </remarks>
public sealed class CodeInterpreterToolDefinition : IFoundryTool
{
    /// <inheritdoc/>
    public Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default)
    {
        var container = new CodeInterpreterToolContainer(new AutomaticCodeInterpreterToolContainerConfiguration());
        return Task.FromResult<ResponseTool>(new CodeInterpreterTool(container));
    }
}

/// <summary>
/// A built-in Foundry tool that enables an agent to search uploaded files
/// and proprietary documents using vector search.
/// </summary>
/// <remarks>
/// This tool requires no Azure provisioning or project connections.
/// Vector store IDs can optionally be configured for specific document collections.
/// </remarks>
public sealed class FileSearchToolDefinition : IFoundryTool
{
    /// <summary>
    /// Gets or sets the vector store IDs to search. If empty, the agent's default stores are used.
    /// </summary>
    public IList<string> VectorStoreIds { get; init; } = [];

    /// <inheritdoc/>
    public Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResponseTool>(ResponseTool.CreateFileSearchTool(VectorStoreIds));
    }
}

/// <summary>
/// A built-in Foundry tool that retrieves real-time information from the public web
/// and returns answers with inline citations.
/// </summary>
/// <remarks>
/// This is the recommended way to add web grounding to an agent.
/// No Azure provisioning is required — the tool is provided by the Foundry Agent Service.
/// </remarks>
public sealed class WebSearchToolDefinition : IFoundryTool
{
    /// <inheritdoc/>
    public Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResponseTool>(ResponseTool.CreateWebSearchTool());
    }
}
