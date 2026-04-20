// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
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

/// <summary>
/// A built-in Foundry tool that enables an agent to generate and edit images.
/// </summary>
/// <remarks>
/// This is an experimental feature and may change in future releases.
/// </remarks>
[Experimental("ASPIREFOUNDRY001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ImageGenerationToolDefinition : IFoundryTool
{
    /// <inheritdoc/>
    public Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResponseTool>(new ImageGenerationTool());
    }
}

/// <summary>
/// A built-in Foundry tool that enables an agent to interact with a computer desktop
/// by taking screenshots, moving the mouse, clicking, and typing.
/// </summary>
/// <remarks>
/// This is an experimental feature and may change in future releases.
/// The computer tool requires specifying the display dimensions and environment.
/// </remarks>
[Experimental("ASPIREFOUNDRY001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ComputerToolDefinition : IFoundryTool
{
    /// <summary>
    /// Creates a new instance of the <see cref="ComputerToolDefinition"/> class.
    /// </summary>
    /// <param name="displayWidth">The width of the display in pixels.</param>
    /// <param name="displayHeight">The height of the display in pixels.</param>
    /// <param name="environment">The environment identifier (e.g., "browser").</param>
    public ComputerToolDefinition(int displayWidth, int displayHeight, string environment = "browser")
    {
        DisplayWidth = displayWidth;
        DisplayHeight = displayHeight;
        Environment = environment;
    }

    /// <summary>
    /// Gets the width of the display in pixels.
    /// </summary>
    public int DisplayWidth { get; }

    /// <summary>
    /// Gets the height of the display in pixels.
    /// </summary>
    public int DisplayHeight { get; }

    /// <summary>
    /// Gets the environment identifier.
    /// </summary>
    public string Environment { get; }

    /// <inheritdoc/>
    public Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResponseTool>(
            new ComputerTool(new ComputerToolEnvironment(Environment), DisplayWidth, DisplayHeight));
    }
}
