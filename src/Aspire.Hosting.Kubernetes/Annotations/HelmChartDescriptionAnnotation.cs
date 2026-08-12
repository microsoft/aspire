// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// An annotation placed on a <see cref="KubernetesEnvironmentResource"/> that specifies
/// the Helm chart description written to the generated <c>Chart.yaml</c>.
/// </summary>
/// <param name="description">A <see cref="ReferenceExpression"/> representing the chart description value.</param>
public sealed class HelmChartDescriptionAnnotation(ReferenceExpression description) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Helm chart description as a <see cref="ReferenceExpression"/>.
    /// </summary>
    public ReferenceExpression Description { get; } = description ?? throw new ArgumentNullException(nameof(description));
}
