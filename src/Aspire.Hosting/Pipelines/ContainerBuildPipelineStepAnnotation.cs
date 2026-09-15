// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// The <see cref="PipelineStepAnnotation"/> that contributes the container image build and push steps.
/// </summary>
/// <remarks>
/// <para>
/// This exists purely to give <c>EnsureBuildAndPushPipelineAnnotations</c> an annotation slot it owns.
/// <see cref="PipelineStepAnnotation"/> is a multi-instance annotation: resource constructors, integrations, and the
/// public <c>WithPipelineStep</c> APIs all append their own, so the collection routinely holds several unrelated
/// instances. Identifying "the build and push steps" by the base type would therefore match somebody else's step.
/// </para>
/// <para>
/// Deriving rather than wrapping keeps this invisible to consumers: the pipeline collects steps by reading
/// <see cref="PipelineStepAnnotation"/>, so a derived instance participates exactly like any other.
/// </para>
/// </remarks>
internal sealed class ContainerBuildPipelineStepAnnotation(
    Func<PipelineStepFactoryContext, IEnumerable<PipelineStep>> factory)
    : PipelineStepAnnotation(factory);
