// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a metric specification used for horizontal scaling in Kubernetes.
/// Supports various types of metrics for monitoring and scaling, such as container resource metrics,
/// external metrics, pod metrics, object metrics, and resource metrics.
/// </summary>
/// <remarks>
/// Kubernetes requires that only the metric source matching <see cref="Type"/> is populated, so the
/// metric source properties are <see langword="null"/> by default and only the ones that are set are emitted.
/// </remarks>
[YamlSerializable]
public sealed class MetricSpecV2
{
    /// <summary>
    /// Represents a container resource metric source for Kubernetes scaling.
    /// This property provides the configuration needed to scale workloads based
    /// on resource utilization metrics associated with a specific container within a pod.
    /// </summary>
    [YamlMember(Alias = "containerResource")]
    public ContainerResourceMetricSourceV2? ContainerResource { get; set; }

    /// <summary>
    /// Represents a resource-based metric source used for autoscaling in Kubernetes environments.
    /// This property defines how the resource metrics should be retrieved and targeted
    /// for scaling purposes.
    /// </summary>
    [YamlMember(Alias = "resource")]
    public ResourceMetricSourceV2? Resource { get; set; }

    /// <summary>
    /// Specifies the type of metric source used for scaling in Kubernetes.
    /// The value of this property determines the nature of the scaling metric
    /// and corresponds to one of the metric sources available in the
    /// MetricSpecV2 class (e.g., "ContainerResource", "Resource", "External",
    /// "Pods", or "Object").
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = null!;

    /// <summary>
    /// Defines the configuration for an external metric source in the Kubernetes MetricSpecV2.
    /// </summary>
    /// <remarks>
    /// The <c>External</c> property specifies an external metric to be used for
    /// scaling behavior in Kubernetes, where the metric originates from an outside
    /// monitoring system or component. It allows the definition of the metric name,
    /// associated selectors, and the desired target values for autoscaling.
    /// This provides flexibility in extending metric sources beyond standard Kubernetes
    /// resource and container metrics.
    /// </remarks>
    [YamlMember(Alias = "external")]
    public ExternalMetricSourceV2? External { get; set; }

    /// <summary>
    /// Represents a metric source targeted at Kubernetes Pods.
    /// Provides scalable metrics for workloads at the Pod level, enabling
    /// monitoring and adjustment based on specific scale conditions of pod metrics.
    /// </summary>
    [YamlMember(Alias = "pods")]
    public PodsMetricSourceV2? Pods { get; set; }

    /// <summary>
    /// Gets or sets the Object metric source.
    /// The Object metric source indicates a metric that is measured on a specific Kubernetes object
    /// (for example, hits-per-second on an Ingress object). The current value of the metric is
    /// obtained from the described Kubernetes object.
    /// </summary>
    [YamlMember(Alias = "object")]
    public ObjectMetricSourceV2? Object { get; set; }
}
