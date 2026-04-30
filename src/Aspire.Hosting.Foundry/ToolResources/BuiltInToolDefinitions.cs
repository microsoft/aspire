// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Foundry tool resource that enables an agent to write and run Python code
/// in a sandboxed environment for data analysis, math, and chart generation.
/// </summary>
/// <remarks>
/// This tool requires no Azure provisioning or project connections.
/// It is automatically available in all Foundry projects.
/// </remarks>
[AspireExport]
public sealed class CodeInterpreterToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="CodeInterpreterToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    public CodeInterpreterToolResource([ResourceName] string name, AzureCognitiveServicesProjectResource project)
        : base(name, project)
    {
    }

    /// <inheritdoc/>
    public override Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        var container = new CodeInterpreterToolContainer(new AutomaticCodeInterpreterToolContainerConfiguration());
        return Task.FromResult<ResponseTool>(new CodeInterpreterTool(container));
    }
}

/// <summary>
/// A Foundry tool resource that enables an agent to search uploaded files
/// and proprietary documents using vector search.
/// </summary>
/// <remarks>
/// This tool requires no Azure provisioning or project connections.
/// Vector store IDs can optionally be configured for specific document collections.
/// </remarks>
[AspireExport]
public sealed class FileSearchToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="FileSearchToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    public FileSearchToolResource([ResourceName] string name, AzureCognitiveServicesProjectResource project)
        : base(name, project)
    {
    }

    /// <summary>
    /// Gets the vector store IDs to search. If empty, the agent's default stores are used.
    /// </summary>
    public IList<string> VectorStoreIds { get; init; } = [];

    /// <inheritdoc/>
    public override Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResponseTool>(ResponseTool.CreateFileSearchTool(VectorStoreIds));
    }
}

/// <summary>
/// A Foundry tool resource that retrieves real-time information from the public web
/// and returns answers with inline citations.
/// </summary>
/// <remarks>
/// This is the recommended way to add web grounding to an agent.
/// No Azure provisioning is required — the tool is provided by the Foundry Agent Service.
/// </remarks>
[AspireExport]
public sealed class WebSearchToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="WebSearchToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    public WebSearchToolResource([ResourceName] string name, AzureCognitiveServicesProjectResource project)
        : base(name, project)
    {
    }

    /// <inheritdoc/>
    public override Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResponseTool>(ResponseTool.CreateWebSearchTool());
    }
}

/// <summary>
/// A Foundry tool resource that enables an agent to generate and edit images.
/// </summary>
[AspireExport]
public sealed class ImageGenerationToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="ImageGenerationToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    public ImageGenerationToolResource([ResourceName] string name, AzureCognitiveServicesProjectResource project)
        : base(name, project)
    {
    }

    /// <inheritdoc/>
    public override Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResponseTool>(new ImageGenerationTool());
    }
}

/// <summary>
/// A Foundry tool resource that enables an agent to interact with a computer desktop
/// by taking screenshots, moving the mouse, clicking, and typing.
/// </summary>
/// <remarks>
/// The computer tool requires specifying the display dimensions and environment.
/// </remarks>
[AspireExport]
public sealed class ComputerToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="ComputerToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    /// <param name="displayWidth">The width of the display in pixels.</param>
    /// <param name="displayHeight">The height of the display in pixels.</param>
    /// <param name="environment">The environment identifier (e.g., "browser").</param>
    public ComputerToolResource(
        [ResourceName] string name,
        AzureCognitiveServicesProjectResource project,
        int displayWidth = 1024,
        int displayHeight = 768,
        string environment = "browser")
        : base(name, project)
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
    public override Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResponseTool>(
            new ComputerTool(new ComputerToolEnvironment(Environment), DisplayWidth, DisplayHeight));
    }
}
