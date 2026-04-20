// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Represents a Foundry tool that can be attached to a prompt agent.
/// </summary>
/// <remarks>
/// This is the base interface for all Foundry tools, both resource-backed
/// (requiring Azure provisioning or project connections) and lightweight
/// built-in tools (like Code Interpreter or File Search).
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
    /// <param name="context">The pipeline step context for resolving deploy-time values.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The SDK tool representation.</returns>
    Task<ResponseTool> ToAgentToolAsync(PipelineStepContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a Foundry tool that is also an Aspire resource, meaning it participates in
/// the application model and may require Azure provisioning or project connections.
/// </summary>
public interface IFoundryToolResource : IResource, IFoundryTool
{
}
