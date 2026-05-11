// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes.Annotations;

/// <summary>
/// Declares a Kustomize overlay to be applied into the k3s cluster before
/// the <see cref="K3sClusterResource"/> transitions to the running state.
/// </summary>
public sealed class KustomizeAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of <see cref="KustomizeAnnotation"/>.
    /// </summary>
    /// <param name="path">
    /// Path to the Kustomize directory or URL (e.g. <c>./k8s/overlays/local</c> or a GitHub URL).
    /// Relative paths are resolved against the AppHost project directory.
    /// </param>
    public KustomizeAnnotation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>Gets the path or URL passed to <c>kubectl apply -k</c>.</summary>
    public string Path { get; }
}
