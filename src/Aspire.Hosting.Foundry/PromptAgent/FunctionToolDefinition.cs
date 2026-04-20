// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Foundry tool definition that enables an agent to call a user-defined function.
/// </summary>
/// <remarks>
/// Function calling tools allow agents to invoke functions defined by the application.
/// The agent decides when to call the function based on the function name, description,
/// and parameter schema, then returns a structured function call request that the
/// application handles.
/// </remarks>
public sealed class FunctionToolDefinition : IFoundryTool
{
    /// <summary>
    /// Creates a new instance of the <see cref="FunctionToolDefinition"/> class.
    /// </summary>
    /// <param name="functionName">The name of the function.</param>
    /// <param name="parameters">The JSON schema defining the function parameters.</param>
    /// <param name="description">A description of what the function does (used by the agent to decide when to call it).</param>
    /// <param name="strictModeEnabled">Whether to enable strict mode for parameter validation.</param>
    public FunctionToolDefinition(
        string functionName,
        BinaryData parameters,
        string? description = null,
        bool? strictModeEnabled = null)
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
    public Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ResponseTool>(
            ResponseTool.CreateFunctionTool(FunctionName, Parameters, StrictModeEnabled, Description));
    }
}
