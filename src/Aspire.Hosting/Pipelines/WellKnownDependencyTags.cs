// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Defines well-known dependency tags that declare what tooling a pipeline step requires
/// on the CI machine. Pipeline integrations use these tags to heuristically determine
/// which setup steps (e.g., "Setup .NET", "Setup Node.js") to emit in the generated workflow.
/// </summary>
/// <remarks>
/// These tags are orthogonal to <see cref="WellKnownPipelineTags"/>, which categorize
/// <em>what</em> a step does. Dependency tags describe <em>what a step needs</em> to run.
/// </remarks>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class WellKnownDependencyTags
{
    /// <summary>
    /// Indicates the step requires the .NET SDK to be installed on the CI machine.
    /// </summary>
    public const string DotNet = "requires-dotnet";

    /// <summary>
    /// Indicates the step requires Node.js to be installed on the CI machine.
    /// </summary>
    public const string NodeJs = "requires-nodejs";

    /// <summary>
    /// Indicates the step requires Docker to be available on the CI machine.
    /// </summary>
    public const string Docker = "requires-docker";

    /// <summary>
    /// Indicates the step requires the Azure CLI to be available on the CI machine.
    /// </summary>
    public const string AzureCli = "requires-azure-cli";

    /// <summary>
    /// Indicates the step requires the Aspire CLI to be installed on the CI machine.
    /// </summary>
    public const string AspireCli = "requires-aspire-cli";
}
