// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Options that define an Azure Function tool and its Azure Storage queue bindings.
/// </summary>
[AspireDto]
public sealed class AzureFunctionToolOptions
{
    /// <summary>
    /// Gets or initializes the name of the Azure Function.
    /// </summary>
    public required string FunctionName { get; init; }

    /// <summary>
    /// Gets or initializes the description used by the agent to decide when to call the function.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets or initializes the JSON schema defining the function parameters as a JSON string.
    /// </summary>
    public required string ParametersJson { get; init; }

    /// <summary>
    /// Gets or initializes the Azure Storage Queue endpoint for the input binding.
    /// </summary>
    public required string InputQueueEndpoint { get; init; }

    /// <summary>
    /// Gets or initializes the queue name for the input binding.
    /// </summary>
    public required string InputQueueName { get; init; }

    /// <summary>
    /// Gets or initializes the Azure Storage Queue endpoint for the output binding.
    /// </summary>
    public required string OutputQueueEndpoint { get; init; }

    /// <summary>
    /// Gets or initializes the queue name for the output binding.
    /// </summary>
    public required string OutputQueueName { get; init; }
}
