// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents an execution environment for pipeline steps, such as local execution,
/// GitHub Actions, Azure DevOps, or other CI/CD systems.
/// </summary>
/// <remarks>
/// Pipeline environment resources are added to the distributed application model to indicate
/// where pipeline steps should be executed. Use <see cref="PipelineEnvironmentCheckAnnotation"/>
/// to register a relevance check that determines whether this environment is active for the
/// current invocation.
/// </remarks>
[Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IPipelineEnvironment : IResource
{
}
