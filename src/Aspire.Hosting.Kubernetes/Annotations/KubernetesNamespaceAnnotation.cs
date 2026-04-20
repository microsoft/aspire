// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// An annotation placed on a <see cref="KubernetesEnvironmentResource"/> that specifies
/// the target Kubernetes namespace for deployment.
/// </summary>
/// <param name="namespace">A <see cref="ReferenceExpression"/> representing the namespace value.</param>
public sealed class KubernetesNamespaceAnnotation(ReferenceExpression @namespace) : IResourceAnnotation
{
    /// <summary>
    /// Gets the namespace value as a <see cref="ReferenceExpression"/>.
    /// </summary>
    public ReferenceExpression Namespace { get; } = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
}
