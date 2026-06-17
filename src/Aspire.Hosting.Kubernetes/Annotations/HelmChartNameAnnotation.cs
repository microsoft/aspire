// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// An annotation placed on a <see cref="KubernetesEnvironmentResource"/> that specifies
/// the Helm chart name written to the generated <c>Chart.yaml</c>.
/// </summary>
/// <param name="name">A <see cref="ReferenceExpression"/> representing the chart name value.</param>
public sealed class HelmChartNameAnnotation(ReferenceExpression name) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Helm chart name as a <see cref="ReferenceExpression"/>.
    /// </summary>
    public ReferenceExpression Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
}
