// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Provisioning;

/// <summary>
/// Azure infrastructure data supplied to a polyglot customization callback.
/// </summary>
[AspireDto]
internal sealed class AzureInfrastructureCustomizationContext
{
    /// <summary>
    /// Gets the Aspire resource name associated with the infrastructure.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// Gets the serialized Azure provisioning infrastructure document.
    /// </summary>
    public required string InfrastructureJson { get; init; }
}

/// <summary>
/// Azure infrastructure data returned from a polyglot customization callback.
/// </summary>
[AspireDto]
internal sealed class AzureInfrastructureCustomizationResult
{
    /// <summary>
    /// Gets the serialized Azure provisioning infrastructure document.
    /// </summary>
    public required string InfrastructureJson { get; init; }
}
