// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Options for installing a Helm chart into a Kubernetes cluster.
/// </summary>
[AspireExport(ExposeProperties = true)]
public sealed class HelmChartInstallOptions
{
    /// <summary>
    /// Gets or sets the chart reference. This can be a chart name from a repository,
    /// an OCI URL (e.g., <c>oci://quay.io/jetstack/charts/cert-manager</c>),
    /// or a local path.
    /// </summary>
    public string Chart { get; set; } = default!;

    /// <summary>
    /// Gets or sets the chart version to install. If not specified, the latest version is used.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the Kubernetes namespace to install the chart into.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Gets or sets whether to create the namespace if it doesn't exist. Defaults to <c>true</c>.
    /// </summary>
    public bool CreateNamespace { get; set; } = true;

    /// <summary>
    /// Gets the Helm values to set via <c>--set</c> flags.
    /// </summary>
    public Dictionary<string, string> Values { get; } = [];

    /// <summary>
    /// Gets or sets the timeout for the Helm install/upgrade operation.
    /// Defaults to 5 minutes.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets whether to wait for all resources to be ready before marking
    /// the release as successful. Defaults to <c>true</c>.
    /// </summary>
    public bool Wait { get; set; } = true;
}
