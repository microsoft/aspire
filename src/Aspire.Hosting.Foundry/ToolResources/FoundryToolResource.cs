// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Base class for Foundry tool resources that participate in the Aspire application model.
/// </summary>
/// <remarks>
/// All Foundry tools are modeled as project-level resources, enabling dashboard visibility,
/// reusability across agents, and consistent lifecycle management. Create tool instances using
/// the <c>Add*Tool</c> extension methods on <see cref="AzureCognitiveServicesProjectResource"/>.
/// </remarks>
[AspireExport]
public abstract class FoundryToolResource : Resource, IFoundryTool
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
    public abstract Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default);
}
