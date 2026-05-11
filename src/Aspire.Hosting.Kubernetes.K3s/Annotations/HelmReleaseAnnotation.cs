// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes.Annotations;

/// <summary>
/// Declares a Helm chart release to be installed into the k3s cluster before
/// the <see cref="K3sClusterResource"/> transitions to the running state.
/// </summary>
public sealed class HelmReleaseAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of <see cref="HelmReleaseAnnotation"/>.
    /// </summary>
    /// <param name="releaseName">The Helm release name.</param>
    /// <param name="chart">The chart name or OCI reference (e.g. <c>cert-manager</c> or <c>oci://ghcr.io/org/chart</c>).</param>
    /// <param name="repo">Optional Helm repository URL. Required when <paramref name="chart"/> is a short name.</param>
    /// <param name="version">Optional chart version. If omitted, the latest version is used.</param>
    /// <param name="namespace">Kubernetes namespace for the release. Defaults to <c>default</c>.</param>
    /// <param name="valuesFile">Optional path to a values YAML file on the host.</param>
    public HelmReleaseAnnotation(
        string releaseName,
        string chart,
        string? repo = null,
        string? version = null,
        string? @namespace = null,
        string? valuesFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(chart);

        ReleaseName = releaseName;
        Chart = chart;
        Repo = repo;
        Version = version;
        Namespace = @namespace ?? "default";
        ValuesFile = valuesFile;
    }

    /// <summary>Gets the Helm release name.</summary>
    public string ReleaseName { get; }

    /// <summary>Gets the chart name or OCI reference.</summary>
    public string Chart { get; }

    /// <summary>Gets the optional Helm repository URL.</summary>
    public string? Repo { get; }

    /// <summary>Gets the optional chart version.</summary>
    public string? Version { get; }

    /// <summary>Gets the Kubernetes namespace for the release.</summary>
    public string Namespace { get; }

    /// <summary>Gets the optional path to a values YAML file.</summary>
    public string? ValuesFile { get; }
}
