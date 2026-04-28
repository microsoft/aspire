// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Foundry tool resource that enables an agent to call a user-defined function.
/// </summary>
/// <remarks>
/// Function calling tools allow agents to invoke functions defined by the application.
/// The agent decides when to call the function based on the function name, description,
/// and parameter schema, then returns a structured function call request that the
/// application handles.
/// </remarks>
[AspireExport]
public sealed class FunctionToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="FunctionToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    /// <param name="functionName">The name of the function.</param>
    /// <param name="parameters">The JSON schema defining the function parameters.</param>
    /// <param name="description">A description of what the function does.</param>
    /// <param name="strictModeEnabled">Whether to enable strict mode for parameter validation.</param>
    public FunctionToolResource(
        [ResourceName] string name,
        AzureCognitiveServicesProjectResource project,
        string functionName,
        BinaryData parameters,
        string? description = null,
        bool? strictModeEnabled = null)
        : base(name, project)
    {
        ArgumentException.ThrowIfNullOrEmpty(functionName);
        ArgumentNullException.ThrowIfNull(parameters);

        FunctionName = functionName;
        Parameters = parameters;
        Description = description;
        StrictModeEnabled = strictModeEnabled;
    }

    /// <summary>
    /// Gets the name of the function.
    /// </summary>
    public string FunctionName { get; }

    /// <summary>
    /// Gets the JSON schema defining the function parameters.
    /// </summary>
    public BinaryData Parameters { get; }

    /// <summary>
    /// Gets the description of the function.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets whether strict mode is enabled for parameter validation.
    /// </summary>
    public bool? StrictModeEnabled { get; }

    /// <inheritdoc/>
    public override Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResponseTool>(
            ResponseTool.CreateFunctionTool(FunctionName, Parameters, StrictModeEnabled, Description));
    }
}
