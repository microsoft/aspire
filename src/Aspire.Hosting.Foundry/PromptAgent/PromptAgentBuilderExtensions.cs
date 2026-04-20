// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Foundry;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Microsoft Foundry prompt agents and tools to the distributed application model.
/// </summary>
public static class PromptAgentBuilderExtensions
{
    /// <summary>
    /// Adds a prompt agent to a Microsoft Foundry project with the specified tools.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prompt agents are always deployed to Azure Foundry, even during local development
    /// (<c>aspire run</c>). Local services communicate with the cloud-provisioned agent
    /// using the project endpoint and agent name injected as environment variables.
    /// </para>
    /// <para>
    /// Tools are project-level resources created with <c>Add*Tool</c> methods and can be
    /// reused across multiple agents in the same project.
    /// </para>
    /// </remarks>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the parent Microsoft Foundry project resource.</param>
    /// <param name="model">The model deployment to use for this agent.</param>
    /// <param name="name">The name of the prompt agent. This will be the agent name in Foundry.</param>
    /// <param name="instructions">Optional system instructions for the agent.</param>
    /// <param name="tools">The tools to attach to this agent. Use project-level <c>Add*Tool</c> methods to create tools.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the prompt agent resource.</returns>
    /// <example>
    /// <code>
    /// var foundry = builder.AddFoundry("aif");
    /// var project = foundry.AddProject("proj");
    /// var chat = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);
    ///
    /// var bing = project.AddBingGroundingTool("bing").WithReference(bingConnection);
    /// var aiSearch = project.AddAISearchTool("search").WithReference(searchResource);
    /// var codeInterp = project.AddCodeInterpreterTool("code-interp");
    ///
    /// project.AddPromptAgent(chat, "joker-agent",
    ///     instructions: "You are good at telling jokes.",
    ///     tools: [bing, aiSearch, codeInterp]);
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a prompt agent to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzurePromptAgentResource> AddPromptAgent(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        IResourceBuilder<FoundryDeploymentResource> model,
        string name,
        string? instructions = null,
        params IResourceBuilder<FoundryToolResource>[] tools)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var agent = new AzurePromptAgentResource(name, model.Resource.DeploymentName, project.Resource, instructions);

        var agentBuilder = project.ApplicationBuilder.AddResource(agent)
            .WithReferenceRelationship(project)
            .WithReference(project);

        foreach (var tool in tools)
        {
            agent.AddTool(tool.Resource);
            agentBuilder.WithReferenceRelationship(tool);
        }

        return agentBuilder;
    }

    // ──────────────────────────────────────────────────────────────
    // Built-in tools (no Azure provisioning required)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a Code Interpreter tool to a Microsoft Foundry project, enabling agents to write and
    /// run Python code in a sandboxed environment for data analysis, math, and chart generation.
    /// </summary>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExport(Description = "Adds a Code Interpreter tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<CodeInterpreterToolResource> AddCodeInterpreterTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new CodeInterpreterToolResource(name, project.Resource);
        return project.ApplicationBuilder.AddResource(resource)
            .WithReferenceRelationship(project);
    }

    /// <summary>
    /// Adds a File Search tool to a Microsoft Foundry project, enabling agents to search
    /// uploaded files and proprietary documents using vector search.
    /// </summary>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="vectorStoreIds">Optional vector store IDs to search. If empty, the agent's default stores are used.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExport(Description = "Adds a File Search tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<FileSearchToolResource> AddFileSearchTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name,
        params string[] vectorStoreIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new FileSearchToolResource(name, project.Resource)
        {
            VectorStoreIds = vectorStoreIds.ToList()
        };
        return project.ApplicationBuilder.AddResource(resource)
            .WithReferenceRelationship(project);
    }

    /// <summary>
    /// Adds a Web Search tool to a Microsoft Foundry project, enabling agents to retrieve
    /// real-time information from the public web and return answers with inline citations.
    /// </summary>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExport(Description = "Adds a Web Search tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<WebSearchToolResource> AddWebSearchTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new WebSearchToolResource(name, project.Resource);
        return project.ApplicationBuilder.AddResource(resource)
            .WithReferenceRelationship(project);
    }

    /// <summary>
    /// Adds an Image Generation tool to a Microsoft Foundry project, enabling agents to
    /// generate and edit images.
    /// </summary>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [Experimental("ASPIREFOUNDRY001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(Description = "Adds an Image Generation tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<ImageGenerationToolResource> AddImageGenerationTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new ImageGenerationToolResource(name, project.Resource);
        return project.ApplicationBuilder.AddResource(resource)
            .WithReferenceRelationship(project);
    }

    /// <summary>
    /// Adds a Computer Use tool to a Microsoft Foundry project, enabling agents to interact
    /// with a computer desktop by taking screenshots, moving the mouse, clicking, and typing.
    /// </summary>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="displayWidth">The width of the display in pixels.</param>
    /// <param name="displayHeight">The height of the display in pixels.</param>
    /// <param name="environment">The environment identifier. Defaults to "browser".</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [Experimental("ASPIREFOUNDRY001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(Description = "Adds a Computer Use tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<ComputerToolResource> AddComputerUseTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name,
        int displayWidth = 1024,
        int displayHeight = 768,
        string environment = "browser")
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new ComputerToolResource(name, project.Resource, displayWidth, displayHeight, environment);
        return project.ApplicationBuilder.AddResource(resource)
            .WithReferenceRelationship(project);
    }

    // ──────────────────────────────────────────────────────────────
    // Resource-backed tools (require Azure backing resource)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds an Azure AI Search tool to a Microsoft Foundry project, enabling agents to
    /// ground their responses using data from an Azure AI Search index.
    /// </summary>
    /// <remarks>
    /// After creating the tool, call <see cref="WithReference(IResourceBuilder{AzureAISearchToolResource}, IResourceBuilder{AzureSearchResource})"/>
    /// to link it to the backing Azure AI Search resource.
    /// </remarks>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExport(Description = "Adds an Azure AI Search tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureAISearchToolResource> AddAISearchTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new AzureAISearchToolResource(name, project.Resource);
        return project.ApplicationBuilder.AddResource(resource)
            .WithReferenceRelationship(project);
    }

    /// <summary>
    /// Links an Azure AI Search tool to a backing <see cref="AzureSearchResource"/>,
    /// creating the necessary Foundry project connection and role assignments.
    /// </summary>
    /// <param name="tool">The Azure AI Search tool resource builder.</param>
    /// <param name="search">The Azure AI Search resource to use for grounding.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Links an Azure AI Search tool to a backing search resource.")]
    public static IResourceBuilder<AzureAISearchToolResource> WithReference(
        this IResourceBuilder<AzureAISearchToolResource> tool,
        IResourceBuilder<AzureSearchResource> search)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(search);

        if (tool.Resource.Connection is not null)
        {
            throw new InvalidOperationException(
                $"Azure AI Search tool '{tool.Resource.Name}' already has a backing resource configured.");
        }

        // Find the project builder to create the connection
        var projectBuilder = tool.ApplicationBuilder.CreateResourceBuilder(tool.Resource.Project);

        // AddConnection(IResourceBuilder<AzureSearchResource>) already handles role assignments
        var connection = projectBuilder.AddConnection(search);

        tool.Resource.Connection = connection.Resource;
        tool.Resource.SearchResource = search.Resource;
        return tool;
    }

    /// <summary>
    /// Adds a Bing Grounding tool to a Microsoft Foundry project, enabling agents to
    /// ground their responses using Bing Search results.
    /// </summary>
    /// <remarks>
    /// After creating the tool, call <see cref="WithReference(IResourceBuilder{BingGroundingToolResource}, IResourceBuilder{AzureCognitiveServicesProjectConnectionResource})"/>
    /// to link it to a Bing Search connection.
    /// </remarks>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExport(Description = "Adds a Bing Grounding tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<BingGroundingToolResource> AddBingGroundingTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new BingGroundingToolResource(name, project.Resource);
        return project.ApplicationBuilder.AddResource(resource)
            .WithReferenceRelationship(project);
    }

    /// <summary>
    /// Links a Bing Grounding tool to a Foundry project connection for the Bing Search service.
    /// </summary>
    /// <param name="tool">The Bing Grounding tool resource builder.</param>
    /// <param name="bingConnection">The Foundry project connection for the Bing Search service.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("withBingConnectionReference", Description = "Links a Bing Grounding tool to a Bing Search connection.")]
    public static IResourceBuilder<BingGroundingToolResource> WithReference(
        this IResourceBuilder<BingGroundingToolResource> tool,
        IResourceBuilder<AzureCognitiveServicesProjectConnectionResource> bingConnection)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(bingConnection);

        if (tool.Resource.Connection is not null)
        {
            throw new InvalidOperationException(
                $"Bing Grounding tool '{tool.Resource.Name}' already has a connection configured.");
        }

        tool.Resource.Connection = bingConnection.Resource;
        return tool;
    }

    /// <summary>
    /// Links a Bing Grounding tool to a Bing Search resource using its Azure resource ID,
    /// automatically creating the Foundry project connection with the correct authentication
    /// and metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Bing Search resource (<c>Microsoft.Bing/accounts</c>) must be created manually in
    /// the <a href="https://portal.azure.com">Azure portal</a> before using this method.
    /// </para>
    /// <para>
    /// This overload creates a Foundry project connection with <c>ApiKey</c> authentication
    /// and <c>category: "GroundingWithBingSearch"</c>, matching the connection structure
    /// that Azure Foundry expects for Bing grounding.
    /// </para>
    /// </remarks>
    /// <param name="tool">The Bing Grounding tool resource builder.</param>
    /// <param name="bingResourceId">
    /// The full Azure resource ID of the Bing Search resource
    /// (e.g., <c>/subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.Bing/accounts/{name}</c>).
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("withBingResourceIdReference", Description = "Links a Bing Grounding tool to a Bing Search resource by resource ID.")]
    public static IResourceBuilder<BingGroundingToolResource> WithReference(
        this IResourceBuilder<BingGroundingToolResource> tool,
        string bingResourceId)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrEmpty(bingResourceId);

        if (tool.Resource.Connection is not null)
        {
            throw new InvalidOperationException(
                $"Bing Grounding tool '{tool.Resource.Name}' already has a connection configured.");
        }

        var projectBuilder = tool.ApplicationBuilder.CreateResourceBuilder(tool.Resource.Project);
        var connection = projectBuilder.AddBingGroundingConnection($"{tool.Resource.Name}-conn", bingResourceId);
        tool.Resource.Connection = connection.Resource;
        return tool;
    }

    // ──────────────────────────────────────────────────────────────
    // Configuration-only tools
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a SharePoint grounding tool to a Microsoft Foundry project, enabling agents to
    /// search data from SharePoint sites configured as Foundry project connections.
    /// </summary>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="projectConnectionIds">The Foundry project connection IDs for the SharePoint sites.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExport(Description = "Adds a SharePoint grounding tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<SharePointToolResource> AddSharePointTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name,
        params string[] projectConnectionIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new SharePointToolResource(name, project.Resource, projectConnectionIds);
        return project.ApplicationBuilder.AddResource(resource)
            .WithReferenceRelationship(project);
    }

    /// <summary>
    /// Adds a Microsoft Fabric data agent tool to a Microsoft Foundry project, enabling
    /// agents to query data through Fabric data agents.
    /// </summary>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="projectConnectionIds">The Foundry project connection IDs for the Fabric data agents.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExport(Description = "Adds a Microsoft Fabric data agent tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<FabricToolResource> AddFabricTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name,
        params string[] projectConnectionIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new FabricToolResource(name, project.Resource, projectConnectionIds);
        return project.ApplicationBuilder.AddResource(resource)
            .WithReferenceRelationship(project);
    }

    // ──────────────────────────────────────────────────────────────
    // Function tools
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds an Azure Function tool to a Microsoft Foundry project, enabling agents to
    /// invoke a serverless Azure Function with queue-based input/output bindings.
    /// </summary>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="functionName">The name of the Azure Function.</param>
    /// <param name="description">A description of what the function does.</param>
    /// <param name="parameters">The JSON schema defining the function parameters.</param>
    /// <param name="inputQueueEndpoint">The Azure Storage Queue endpoint for input binding.</param>
    /// <param name="inputQueueName">The queue name for input binding.</param>
    /// <param name="outputQueueEndpoint">The Azure Storage Queue endpoint for output binding.</param>
    /// <param name="outputQueueName">The queue name for output binding.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExportIgnore(Reason = "BinaryData parameter is not ATS-compatible.")]
    public static IResourceBuilder<AzureFunctionToolResource> AddAzureFunctionTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name,
        string functionName,
        string description,
        BinaryData parameters,
        string inputQueueEndpoint,
        string inputQueueName,
        string outputQueueEndpoint,
        string outputQueueName)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new AzureFunctionToolResource(
            name, project.Resource, functionName, description, parameters,
            inputQueueEndpoint, inputQueueName, outputQueueEndpoint, outputQueueName);
        return project.ApplicationBuilder.AddResource(resource)
            .WithReferenceRelationship(project);
    }

    /// <summary>
    /// Adds a function calling tool to a Microsoft Foundry project, enabling agents to
    /// call application-defined functions with structured parameters.
    /// </summary>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="functionName">The name of the function.</param>
    /// <param name="parameters">The JSON schema defining the function parameters.</param>
    /// <param name="description">A description of what the function does.</param>
    /// <param name="strictModeEnabled">Whether to enable strict mode for parameter validation.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExportIgnore(Reason = "BinaryData parameter is not ATS-compatible.")]
    public static IResourceBuilder<FunctionToolResource> AddFunctionTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name,
        string functionName,
        BinaryData parameters,
        string? description = null,
        bool? strictModeEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new FunctionToolResource(
            name, project.Resource, functionName, parameters, description, strictModeEnabled);
        return project.ApplicationBuilder.AddResource(resource)
            .WithReferenceRelationship(project);
    }

    // ──────────────────────────────────────────────────────────────
    // Escape hatch: custom IFoundryTool
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a custom tool implementation to a prompt agent using the <see cref="IFoundryTool"/> interface.
    /// </summary>
    /// <remarks>
    /// This is an advanced extensibility point for tools that don't fit the standard
    /// <see cref="FoundryToolResource"/> model. For most scenarios, use the project-level
    /// <c>Add*Tool</c> methods and pass tool resources to <see cref="AddPromptAgent"/>.
    /// </remarks>
    /// <param name="builder">The prompt agent resource builder.</param>
    /// <param name="tool">The custom tool implementation.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "IFoundryTool is not ATS-compatible.")]
    public static IResourceBuilder<AzurePromptAgentResource> WithCustomTool(
        this IResourceBuilder<AzurePromptAgentResource> builder,
        IFoundryTool tool)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tool);

        builder.Resource.CustomTools.Add(tool);
        return builder;
    }
}
