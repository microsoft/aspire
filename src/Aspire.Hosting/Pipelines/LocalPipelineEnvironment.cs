// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Represents the local execution environment for pipeline steps.
/// </summary>
/// <remarks>
/// This is the implicit fallback environment returned by
/// <see cref="DistributedApplicationPipeline.GetEnvironmentAsync(CancellationToken)"/>
/// when no declared <see cref="IPipelineEnvironment"/> resource passes its relevance check.
/// It is not added to the application model.
/// </remarks>
internal sealed class LocalPipelineEnvironment() : Resource("local"), IPipelineEnvironment
{
}
