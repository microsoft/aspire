// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Resources;

namespace Aspire.Hosting.Kubernetes.Annotations;

/// <summary>
/// Represents an annotation for customizing a Kubernetes persistent volume claim.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="KubernetesPersistentVolumeCustomizationAnnotation"/> class.
/// </remarks>
/// <param name="configure">The configuration action for customizing the persistent volume claim.</param>
internal sealed class KubernetesPersistentVolumeCustomizationAnnotation(Action<PersistentVolumeClaim> configure) : IResourceAnnotation
{
    /// <summary>
    /// Gets the configuration action for customizing the persistent volume claim.
    /// </summary>
    public Action<PersistentVolumeClaim> Configure { get; } = configure ?? throw new ArgumentNullException(nameof(configure));
}