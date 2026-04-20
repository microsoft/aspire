// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Foundry;
using Azure.Provisioning.Search;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Microsoft Foundry prompt agents and tools to the distributed application model.
/// </summary>
public static class PromptAgentBuilderExtensions
{
    /// <summary>
    /// Adds a prompt agent to a Microsoft Foundry project.
    /// </summary>
    /// <remarks>
    /// Prompt agents are always deployed to Azure Foundry, even during local development
    /// (<c>aspire run</c>). Local services communicate with the cloud-provisioned agent
    /// using the project endpoint and agent name injected as environment variables.
    /// Use <c>WithTool</c> or built-in tool methods to add tools to the agent.
    /// </remarks>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the parent Microsoft Foundry project resource.</param>
    /// <param name="name">The name of the prompt agent. This will be the agent name in Foundry.</param>
    /// <param name="model">The model deployment to use for this agent.</param>
    /// <param name="instructions">Optional system instructions for the agent.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the prompt agent resource.</returns>
    /// <example>
    /// <code>
    /// var foundry = builder.AddFoundry("aif");
    /// var project = foundry.AddProject("proj");
    /// var chat = project.AddModelDeployment("gpt41", FoundryModel.OpenAI.Gpt41);
    ///
    /// var agent = project.AddPromptAgent("joker-agent", chat,
    ///     instructions: "You are good at telling jokes.")
    ///     .WithWebSearch()
    ///     .WithCodeInterpreter();
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a prompt agent to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzurePromptAgentResource> AddPromptAgent(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name,
        IResourceBuilder<FoundryDeploymentResource> model,
        string? instructions = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(model);

        var agent = new AzurePromptAgentResource(name, model.Resource.DeploymentName, project.Resource, instructions);

        return project.ApplicationBuilder.AddResource(agent)
            .WithReferenceRelationship(project)
            .WithReference(project);
    }

    /// <summary>
    /// Adds a Foundry tool to a prompt agent.
    /// </summary>
    /// <remarks>
    /// Use this method to add resource-backed tools (like Azure AI Search) or custom tool
    /// implementations to a prompt agent. For built-in tools, prefer the convenience methods
    /// such as <see cref="WithCodeInterpreter"/>, <see cref="WithFileSearch"/>, or <see cref="WithWebSearch"/>.
    /// </remarks>
    /// <param name="builder">The prompt agent resource builder.</param>
    /// <param name="tool">The tool resource builder to add.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Adds a Foundry tool resource to a prompt agent.")]
    public static IResourceBuilder<AzurePromptAgentResource> WithTool(
        this IResourceBuilder<AzurePromptAgentResource> builder,
        IResourceBuilder<FoundryToolResource> tool)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tool);

        builder.Resource.AddTool(tool.Resource);
        builder.WithReferenceRelationship(tool);
        return builder;
    }

    /// <summary>
    /// Adds a Foundry tool definition to a prompt agent.
    /// </summary>
    /// <remarks>
    /// Use this method to add lightweight, non-resource tool definitions to a prompt agent.
    /// These tools don't require Azure provisioning and are resolved at deploy time.
    /// </remarks>
    /// <param name="builder">The prompt agent resource builder.</param>
    /// <param name="tool">The tool definition to add.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "IFoundryTool is not ATS-compatible.")]
    public static IResourceBuilder<AzurePromptAgentResource> WithTool(
        this IResourceBuilder<AzurePromptAgentResource> builder,
        IFoundryTool tool)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tool);

        builder.Resource.AddTool(tool);
        return builder;
    }

    /// <summary>
    /// Adds the Code Interpreter tool to a prompt agent, enabling it to write and run
    /// Python code in a sandboxed environment for data analysis, math, and chart generation.
    /// </summary>
    /// <param name="builder">The prompt agent resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Adds the Code Interpreter tool to a prompt agent.")]
    public static IResourceBuilder<AzurePromptAgentResource> WithCodeInterpreter(
        this IResourceBuilder<AzurePromptAgentResource> builder)
    {
        return builder.WithTool(new CodeInterpreterToolDefinition());
    }

    /// <summary>
    /// Adds the File Search tool to a prompt agent, enabling it to search uploaded files
    /// and proprietary documents using vector search.
    /// </summary>
    /// <param name="builder">The prompt agent resource builder.</param>
    /// <param name="vectorStoreIds">Optional vector store IDs to search. If empty, the agent's default stores are used.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Adds the File Search tool to a prompt agent.")]
    public static IResourceBuilder<AzurePromptAgentResource> WithFileSearch(
        this IResourceBuilder<AzurePromptAgentResource> builder,
        params string[] vectorStoreIds)
    {
        return builder.WithTool(new FileSearchToolDefinition
        {
            VectorStoreIds = vectorStoreIds.ToList()
        });
    }

    /// <summary>
    /// Adds the Web Search tool to a prompt agent, enabling it to retrieve real-time
    /// information from the public web and return answers with inline citations.
    /// </summary>
    /// <param name="builder">The prompt agent resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Adds the Web Search tool to a prompt agent.")]
    public static IResourceBuilder<AzurePromptAgentResource> WithWebSearch(
        this IResourceBuilder<AzurePromptAgentResource> builder)
    {
        return builder.WithTool(new WebSearchToolDefinition());
    }

    /// <summary>
    /// Adds an Azure AI Search tool to a Microsoft Foundry project, creating the necessary
    /// project connection to the search resource.
    /// </summary>
    /// <remarks>
    /// This method creates both the tool resource and the Foundry project connection
    /// required to use Azure AI Search for grounding agent responses. The search resource
    /// must already be added to the application model.
    /// </remarks>
    /// <param name="project">The <see cref="IResourceBuilder{T}"/> for the Microsoft Foundry project.</param>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="search">The Azure AI Search resource to use for grounding.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the tool resource.</returns>
    [AspireExport(Description = "Adds an Azure AI Search tool to a Microsoft Foundry project.")]
    public static IResourceBuilder<AzureAISearchToolResource> AddAzureAISearchTool(
        this IResourceBuilder<AzureCognitiveServicesProjectResource> project,
        string name,
        IResourceBuilder<AzureSearchResource> search)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(search);

        // Create the Foundry project connection for the search resource
        var connection = project.AddConnection(search);

        // Ensure the project has role assignments to the search resource
        project.WithRoleAssignments(search,
            SearchBuiltInRole.SearchIndexDataReader,
            SearchBuiltInRole.SearchServiceContributor);

        var toolResource = new AzureAISearchToolResource(name, project.Resource, search.Resource)
        {
            Connection = connection.Resource
        };

        return project.ApplicationBuilder.AddResource(toolResource)
            .WithReferenceRelationship(project);
    }
}
