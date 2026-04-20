// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Base class for Foundry tool resources that participate in the Aspire application model.
/// </summary>
/// <remarks>
/// Use this base class for tools that need Azure provisioning or Foundry project connections
/// (e.g., Azure AI Search, Web Search/Bing Grounding). For lightweight built-in tools
/// (e.g., Code Interpreter, File Search), implement <see cref="IFoundryTool"/> directly instead.
/// </remarks>
public abstract class FoundryToolResource : Resource, IFoundryToolResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FoundryToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    protected FoundryToolResource([ResourceName] string name, AzureCognitiveServicesProjectResource project)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(project);
        Project = project;
    }

    /// <summary>
    /// Gets the parent Foundry project resource that this tool is associated with.
    /// </summary>
    public AzureCognitiveServicesProjectResource Project { get; }

    /// <inheritdoc/>
    public abstract Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default);
}
