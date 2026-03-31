// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// An annotation placed on a <see cref="KubernetesEnvironmentResource"/> that captures
/// a callback responsible for creating and wiring all deployment pipeline steps for a
/// specific deployment engine (e.g., Helm, kubectl apply, Kustomize).
/// </summary>
/// <param name="createSteps">
/// A callback that receives the Kubernetes environment resource and a pipeline step factory context,
/// and returns the deployment pipeline steps for the engine.
/// </param>
public sealed class KubernetesDeploymentEngineAnnotation(
    Func<KubernetesEnvironmentResource, PipelineStepFactoryContext, Task<IReadOnlyList<PipelineStep>>> createSteps) : IResourceAnnotation
{
    /// <summary>
    /// Gets the callback that creates deployment pipeline steps for this engine.
    /// </summary>
    public Func<KubernetesEnvironmentResource, PipelineStepFactoryContext, Task<IReadOnlyList<PipelineStep>>> CreateSteps { get; } =
        createSteps ?? throw new ArgumentNullException(nameof(createSteps));
}
