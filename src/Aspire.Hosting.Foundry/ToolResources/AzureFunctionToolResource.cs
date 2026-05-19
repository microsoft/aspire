// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.AI.Projects.Agents;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Foundry tool resource that enables an agent to invoke an Azure Function.
/// </summary>
/// <remarks>
/// Azure Functions tools allow agents to call serverless functions as tools.
/// The function definition includes the function name, parameters schema,
/// and input/output bindings for Azure Storage queues.
/// </remarks>
[AspireExport]
public sealed class AzureFunctionToolResource : FoundryToolResource
{
    /// <summary>
    /// Creates a new instance of the <see cref="AzureFunctionToolResource"/> class.
    /// </summary>
    /// <param name="name">The name of the tool resource.</param>
    /// <param name="project">The parent Foundry project resource.</param>
    /// <param name="functionName">The name of the Azure Function.</param>
    /// <param name="description">A description of what the function does.</param>
    /// <param name="parameters">The JSON schema defining the function parameters.</param>
    /// <param name="inputQueueEndpoint">The Azure Storage Queue endpoint for input binding.</param>
    /// <param name="inputQueueName">The queue name for input binding.</param>
    /// <param name="outputQueueEndpoint">The Azure Storage Queue endpoint for output binding.</param>
    /// <param name="outputQueueName">The queue name for output binding.</param>
    public AzureFunctionToolResource(
        [ResourceName] string name,
        AzureCognitiveServicesProjectResource project,
        string functionName,
        string description,
        BinaryData parameters,
        string inputQueueEndpoint,
        string inputQueueName,
        string outputQueueEndpoint,
        string outputQueueName)
        : base(name, project)
    {
        ArgumentException.ThrowIfNullOrEmpty(functionName);
        ArgumentException.ThrowIfNullOrEmpty(description);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(inputQueueEndpoint);
        ArgumentException.ThrowIfNullOrEmpty(inputQueueName);
        ArgumentException.ThrowIfNullOrEmpty(outputQueueEndpoint);
        ArgumentException.ThrowIfNullOrEmpty(outputQueueName);

        FunctionName = functionName;
        Description = description;
        Parameters = parameters;
        InputQueueEndpoint = inputQueueEndpoint;
        InputQueueName = inputQueueName;
        OutputQueueEndpoint = outputQueueEndpoint;
        OutputQueueName = outputQueueName;
    }

    /// <summary>
    /// Gets the name of the Azure Function.
    /// </summary>
    public string FunctionName { get; }

    /// <summary>
    /// Gets the description of the function (used by the agent to decide when to call it).
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the JSON schema defining the function parameters.
    /// </summary>
    public BinaryData Parameters { get; }

    /// <summary>
    /// Gets the Azure Storage Queue endpoint for input binding.
    /// </summary>
    public string InputQueueEndpoint { get; }

    /// <summary>
    /// Gets the queue name for input binding.
    /// </summary>
    public string InputQueueName { get; }

    /// <summary>
    /// Gets the Azure Storage Queue endpoint for output binding.
    /// </summary>
    public string OutputQueueEndpoint { get; }

    /// <summary>
    /// Gets the queue name for output binding.
    /// </summary>
    public string OutputQueueName { get; }

    /// <inheritdoc/>
    public override Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default)
    {
        var function = new AzureFunctionDefinitionFunction(FunctionName, Parameters)
        {
            Description = Description
        };
        var inputBinding = new AzureFunctionBinding(
            new AzureFunctionStorageQueue(InputQueueEndpoint, InputQueueName));
        var outputBinding = new AzureFunctionBinding(
            new AzureFunctionStorageQueue(OutputQueueEndpoint, OutputQueueName));

        var definition = new AzureFunctionDefinition(function, inputBinding, outputBinding);
        return Task.FromResult<ResponseTool>(new AzureFunctionTool(definition));
    }
}
