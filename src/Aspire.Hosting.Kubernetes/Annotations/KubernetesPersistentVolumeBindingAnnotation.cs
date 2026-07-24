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
internal sealed class KubernetesPersistentVolumeBindingAnnotation(KubernetesPersistentVolumeResource volume) : IResourceAnnotation
{
    /// <summary>
    /// Gets the persistent volume resource bound to the workload.
    /// </summary>
    public KubernetesPersistentVolumeResource Volume { get; } = volume ?? throw new ArgumentNullException(nameof(volume));
}
