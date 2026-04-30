// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Represents a Foundry tool that can be attached to a prompt agent.
/// </summary>
/// <remarks>
/// This interface provides an extensibility point for custom tool implementations
/// that don't fit the standard <see cref="FoundryToolResource"/> model.
/// For most scenarios, use the project-level <c>Add*Tool</c> methods which return
/// <see cref="FoundryToolResource"/> subclasses.
/// </remarks>
public interface IFoundryTool
{
    /// <summary>
    /// Converts this tool definition into the SDK <see cref="ResponseTool"/> representation.
    /// </summary>
    /// <remarks>
    /// This method is called at deploy time, after infrastructure provisioning is complete.
    /// Tools that depend on provisioned resources (e.g., Azure AI Search connections) can
    /// safely resolve their connection identifiers at this point.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The SDK tool representation.</returns>
    Task<ResponseTool> ToAgentToolAsync(CancellationToken cancellationToken = default);
}
