// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents the update strategy configuration for a Kubernetes StatefulSet resource.
/// </summary>
/// <remarks>
/// This class defines how updates to the StatefulSet's pods are applied.
/// Kubernetes provides various strategies to update pods in a StatefulSet, enabling controlled and efficient updates to deployed applications.
/// </remarks>
[YamlSerializable]
public sealed class StatefulSetUpdateStrategyV1
{
    /// <summary>
    /// Specifies the rolling update strategy configuration for a StatefulSet in Kubernetes.
    /// </summary>
    /// <remarks>
    /// This property defines the parameters for the rolling update process, managing how Pods
    /// in a StatefulSet are updated with minimal downtime and service disruption.
    /// Defaults to <see langword="null"/> so the property is omitted from the serialized YAML when
    /// no rolling-update parameters have been configured. Eagerly initializing this to <c>new()</c>
    /// would otherwise emit an invalid empty <c>rollingUpdate: {}</c> mapping that Kubernetes
    /// rejects on apply.
    /// </remarks>
    [YamlMember(Alias = "rollingUpdate")]
    public RollingUpdateStatefulSetStrategyV1? RollingUpdate { get; set; }

    /// <summary>
    /// Determines the type of update strategy for a StatefulSet in Kubernetes.
    /// </summary>
    /// <remarks>
    /// This property specifies the update strategy type used for managing updates to the StatefulSet.
    /// Common values include "RollingUpdate" for rolling updates of the StatefulSet pods, or "OnDelete" for manual updates.
    /// </remarks>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "RollingUpdate";
}
