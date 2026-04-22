// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// An annotation that stores the configuration for a Helm chart to be installed
/// into the Kubernetes cluster before the application's Helm chart is deployed.
/// </summary>
/// <param name="releaseName">The Helm release name.</param>
/// <param name="options">The chart configuration options.</param>
public sealed class HelmChartAnnotation(string releaseName, HelmChartInstallOptions options) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Helm release name.
    /// </summary>
    public string ReleaseName { get; } = releaseName;

    /// <summary>
    /// Gets the chart configuration options.
    /// </summary>
    public HelmChartInstallOptions Options { get; } = options;
}
