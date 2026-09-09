// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes.Annotations;

/// <summary>
/// Annotation that binds a workload resource to a
/// <see cref="KubernetesPersistentVolumeResource"/>. The Kubernetes publisher reads
/// these annotations during volume processing to decide whether a pod's
/// <c>volumes[]</c> entry should reference the PVC generated from a first-class
/// volume resource (rather than the environment's default storage type) and to
/// promote the workload to a <c>StatefulSet</c>.
/// </summary>
/// <param name="volume">The persistent volume resource the workload binds to.</param>
/// <param name="environmentVariableName">The environment variable that exposes the effective mount path, if configured.</param>
/// <param name="runModeContainerVolumeName">The worktree-scoped container volume name to apply in run mode, if required.</param>
internal sealed class KubernetesPersistentVolumeBindingAnnotation(
    KubernetesPersistentVolumeResource volume,
    string? environmentVariableName = null,
    string? runModeContainerVolumeName = null) : IResourceAnnotation
{
    /// <summary>
    /// Gets the persistent volume resource bound to the workload.
    /// </summary>
    public KubernetesPersistentVolumeResource Volume { get; } = volume ?? throw new ArgumentNullException(nameof(volume));

    /// <summary>
    /// Gets the environment variable that exposes the effective mount path, if configured.
    /// </summary>
    public string? EnvironmentVariableName { get; } = environmentVariableName;

    /// <summary>
    /// Gets the worktree-scoped container volume name to apply in run mode, if required.
    /// </summary>
    public string? RunModeContainerVolumeName { get; } = runModeContainerVolumeName;
}
