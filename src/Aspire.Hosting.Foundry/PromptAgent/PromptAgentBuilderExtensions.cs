// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable OPENAI001 // Responses API is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Foundry;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;

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
    ///     instructions: "You are good at telling jokes.")
    ///     .WithTool(bing)
    ///     .WithTool(aiSearch)
    ///     .WithTool(codeInterp);
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a prompt agent to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzurePromptAgentResource> AddPromptAgent(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        IResourceBuilder<FoundryDeploymentResource> model,
        string name,
        string? instructions = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var agent = new AzurePromptAgentResource(name, model.Resource.DeploymentName, project.Resource, instructions);

        var agentBuilder = project.ApplicationBuilder.AddResource(agent)
            .WithReferenceRelationship(project)
            .WithReference(project);

        // Add "Send Message" command to the dashboard (like hosted agents)
        agentBuilder.WithCommand(
            name: "send-message",
            displayName: "Send Message",
            executeCommand: async ctx =>
            {
                var interactionService = ctx.ServiceProvider.GetRequiredService<IInteractionService>();
                var inputResult = await interactionService.PromptInputAsync(
                    title: "Prompt Agent",
                    message: $"Enter a message to send to '{name}'.",
                    inputLabel: "Message",
                    placeHolder: "Hello, what can you do?",
                    cancellationToken: ctx.CancellationToken
                ).ConfigureAwait(false);

                if (inputResult.Canceled || string.IsNullOrWhiteSpace(inputResult.Data.Value))
                {
                    return new ExecuteCommandResult { Success = true };
                }

                try
                {
                    var endpoint = await agent.Project.Endpoint.GetValueAsync(ctx.CancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(endpoint))
                    {
                        throw new InvalidOperationException("Project endpoint is not available.");
                    }

                    var projectClient = new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential());
                    var agentRef = new AgentReference(name: name);
                    var responseClient = projectClient.ProjectOpenAIClient.GetProjectResponsesClientForAgent(agentRef);
                    var response = await responseClient.CreateResponseAsync(inputResult.Data.Value, cancellationToken: ctx.CancellationToken).ConfigureAwait(false);
                    var outputText = response.Value.GetOutputText();

                    await interactionService.PromptMessageBoxAsync(
                        title: $"Response from '{name}'",
                        message: outputText,
                        options: new()
                        {
                            Intent = MessageIntent.Success,
                            EnableMessageMarkdown = true,
                            PrimaryButtonText = "OK"
                        },
                        cancellationToken: ctx.CancellationToken
                    ).ConfigureAwait(false);

                    return new ExecuteCommandResult { Success = true };
                }
                catch (Exception ex)
                {
                    await interactionService.PromptMessageBoxAsync(
                        title: "Error",
                        message: $"Failed to invoke agent: {ex.Message}",
                        options: new()
                        {
                            Intent = MessageIntent.Error,
                            PrimaryButtonText = "OK"
                        },
                        cancellationToken: ctx.CancellationToken
                    ).ConfigureAwait(false);
                    return new ExecuteCommandResult { Success = false };
                }
            },
            commandOptions: new()
            {
                IconName = "Agents",
                IconVariant = IconVariant.Regular,
                IsHighlighted = true,
            }
        );

        // Add Foundry portal URL for the agent (populated after deployment when subscription info is available)
        // The actual URL is set after provisioning completes.

        return agentBuilder;
    }

    /// <summary>
    /// Adds a tool to a prompt agent.
    /// </summary>
    /// <param name="agent">The prompt agent resource builder.</param>
    /// <param name="tool">The tool resource to attach to this agent.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Adds a tool to a prompt agent.")]
    public static IResourceBuilder<AzurePromptAgentResource> WithTool(
        this IResourceBuilder<AzurePromptAgentResource> agent,
        IResourceBuilder<FoundryToolResource> tool)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(tool);

        agent.Resource.AddTool(tool.Resource);
        agent.WithReferenceRelationship(tool);

        return agent;
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
    /// <param name="indexName">Optional name of the search index to query. If not specified, the agent must be told which index to use.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExport(Description = "Adds an Azure AI Search tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureAISearchToolResource> AddAISearchTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name,
        string? indexName = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new AzureAISearchToolResource(name, project.Resource)
        {
            IndexName = indexName
        };
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
    /// After creating the tool, call one of the <c>WithReference</c> overloads
    /// to link it to a Bing Search resource.
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
    /// <param name="bingConnection">
    /// The Bing grounding connection resource builder created by
    /// <see cref="AzureCognitiveServicesProjectConnectionsBuilderExtensions.AddBingGroundingConnection(IResourceBuilder{AzureCognitiveServicesProjectResource}, string, string)"/>
    /// or
    /// <see cref="AzureCognitiveServicesProjectConnectionsBuilderExtensions.AddBingGroundingConnection(IResourceBuilder{AzureCognitiveServicesProjectResource}, string, IResourceBuilder{ParameterResource})"/>.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Covered by the internal AspireUnion overload.")]
    public static IResourceBuilder<BingGroundingToolResource> WithReference(
        this IResourceBuilder<BingGroundingToolResource> tool,
        IResourceBuilder<BingGroundingConnectionResource> bingConnection)
    {
        return WithReference(tool, (object)bingConnection);
    }

    /// <summary>
    /// Links a Bing Grounding tool to a Bing Search resource by using its Azure resource ID.
    /// </summary>
    /// <param name="tool">The Bing Grounding tool resource builder.</param>
    /// <param name="bingResourceId">
    /// The full Azure resource ID of the Bing Search resource
    /// (e.g., <c>/subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.Bing/accounts/{name}</c>).
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Covered by the internal AspireUnion overload.")]
    public static IResourceBuilder<BingGroundingToolResource> WithReference(
        this IResourceBuilder<BingGroundingToolResource> tool,
        string bingResourceId)
    {
        return WithReference(tool, (object)bingResourceId);
    }

    /// <summary>
    /// Links a Bing Grounding tool to a Bing Search resource by using a parameter that contains
    /// its Azure resource ID.
    /// </summary>
    /// <param name="tool">The Bing Grounding tool resource builder.</param>
    /// <param name="bingResourceId">
    /// A parameter resource containing the full Azure resource ID of the Bing Search resource.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Covered by the internal AspireUnion overload.")]
    public static IResourceBuilder<BingGroundingToolResource> WithReference(
        this IResourceBuilder<BingGroundingToolResource> tool,
        IResourceBuilder<ParameterResource> bingResourceId)
    {
        return WithReference(tool, (object)bingResourceId);
    }

    [AspireExport("withBingReference", MethodName = "withReference", Description = "Links a Bing Grounding tool to a Bing Search resource or connection.")]
    internal static IResourceBuilder<BingGroundingToolResource> WithReference(
        this IResourceBuilder<BingGroundingToolResource> tool,
        [AspireUnion(
            typeof(IResourceBuilder<BingGroundingConnectionResource>),
            typeof(string),
            typeof(IResourceBuilder<ParameterResource>))]
        object bingReference)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(bingReference);

        if (tool.Resource.Connection is not null)
        {
            throw new InvalidOperationException(
                $"Bing Grounding tool '{tool.Resource.Name}' already has a connection configured.");
        }

        switch (bingReference)
        {
            case IResourceBuilder<BingGroundingConnectionResource> connectionBuilder:
                tool.Resource.Connection = connectionBuilder.Resource;
                break;

            case string bingResourceId:
                ArgumentException.ThrowIfNullOrEmpty(bingResourceId);
                var projectBuilder = tool.ApplicationBuilder.CreateResourceBuilder(tool.Resource.Project);
                var connection = projectBuilder.AddBingGroundingConnection($"{tool.Resource.Name}-conn", bingResourceId);
                tool.Resource.Connection = connection.Resource;
                break;

            case IResourceBuilder<ParameterResource> parameterBuilder:
                var paramProjectBuilder = tool.ApplicationBuilder.CreateResourceBuilder(tool.Resource.Project);
                var paramConnection = paramProjectBuilder.AddBingGroundingConnection($"{tool.Resource.Name}-conn", parameterBuilder);
                tool.Resource.Connection = paramConnection.Resource;
                break;

            default:
                throw new ArgumentException(
                    $"Unsupported Bing reference type '{bingReference.GetType().Name}'. " +
                    "Expected IResourceBuilder<BingGroundingConnectionResource>, string, or IResourceBuilder<ParameterResource>.",
                    nameof(bingReference));
        }

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
    [AspireExportIgnore(Reason = "BinaryData parameter is not ATS-compatible. Use the string overload instead.")]
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
    /// Adds an Azure Function tool to a Microsoft Foundry project, enabling agents to
    /// invoke a serverless Azure Function with queue-based input/output bindings.
    /// </summary>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="functionName">The name of the Azure Function.</param>
    /// <param name="description">A description of what the function does.</param>
    /// <param name="parametersJson">The JSON schema defining the function parameters as a JSON string.</param>
    /// <param name="inputQueueEndpoint">The Azure Storage Queue endpoint for input binding.</param>
    /// <param name="inputQueueName">The queue name for input binding.</param>
    /// <param name="outputQueueEndpoint">The Azure Storage Queue endpoint for output binding.</param>
    /// <param name="outputQueueName">The queue name for output binding.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExport(Description = "Adds an Azure Function tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureFunctionToolResource> AddAzureFunctionTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name,
        string functionName,
        string description,
        string parametersJson,
        string inputQueueEndpoint,
        string inputQueueName,
        string outputQueueEndpoint,
        string outputQueueName)
    {
        return project.AddAzureFunctionTool(
            name, functionName, description, BinaryData.FromString(parametersJson),
            inputQueueEndpoint, inputQueueName, outputQueueEndpoint, outputQueueName);
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
    [AspireExportIgnore(Reason = "BinaryData parameter is not ATS-compatible. Use the string overload instead.")]
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

    /// <summary>
    /// Adds a function calling tool to a Microsoft Foundry project, enabling agents to
    /// call application-defined functions with structured parameters.
    /// </summary>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="functionName">The name of the function.</param>
    /// <param name="parametersJson">The JSON schema defining the function parameters as a JSON string.</param>
    /// <param name="description">A description of what the function does.</param>
    /// <param name="strictModeEnabled">Whether to enable strict mode for parameter validation.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExport(Description = "Adds a function calling tool to a Microsoft Foundry project.")]
    internal static IResourceBuilder<FunctionToolResource> AddFunctionTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name,
        string functionName,
        string parametersJson,
        string? description = null,
        bool? strictModeEnabled = null)
    {
        return project.AddFunctionTool(
            name, functionName, BinaryData.FromString(parametersJson), description, strictModeEnabled);
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

        builder.Resource.AddCustomTool(tool);
        return builder;
    }
}
