// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// An annotation placed on a <see cref="KubernetesEnvironmentResource"/> that specifies
/// the Helm release name for deployment.
/// </summary>
/// <param name="releaseName">A <see cref="ReferenceExpression"/> representing the release name value.</param>
public sealed class HelmReleaseNameAnnotation(ReferenceExpression releaseName) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Helm release name as a <see cref="ReferenceExpression"/>.
    /// </summary>
    public ReferenceExpression ReleaseName { get; } = releaseName ?? throw new ArgumentNullException(nameof(releaseName));
}
