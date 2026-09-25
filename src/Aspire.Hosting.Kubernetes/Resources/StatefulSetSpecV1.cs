// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents the specification of a Kubernetes StatefulSet resource.
/// </summary>
/// <remarks>
/// A Kubernetes StatefulSet is a workload API object used to manage stateful applications.
/// StatefulSets manage the deployment and scaling of a set of pods, and provide guarantees about the ordering and uniqueness of these pods.
/// </remarks>
[YamlSerializable]
public sealed class StatefulSetSpecV1
{
    /// <summary>
    /// Gets or sets the name of the governing service for the StatefulSet.
    /// The service name is used to maintain the network identity of each pod
    /// and is required for controlling the storage and network identities of the pods
    /// in the StatefulSet. Each pod will inherit its DNS hostname from this service name.
    /// </summary>
    [YamlMember(Alias = "serviceName")]
    public string ServiceName { get; set; } = null!;

    /// <summary>
    /// Specifies the pod template used by the StatefulSet for creating pods.
    /// </summary>
    /// <remarks>
    /// This property defines the structure and configuration of the pod that will be created by the StatefulSet. It includes metadata
    /// and specifications for each pod, ensuring consistency and desired behavior for all pods managed by the StatefulSet.
    /// </remarks>
    [YamlMember(Alias = "template")]
    public PodTemplateSpecV1 Template { get; set; } = new();

    /// <summary>
    /// Gets or sets the label selector that is used to identify the set of pods targeted by the StatefulSet.
    /// </summary>
    /// <remarks>
    /// The <c>Selector</c> property defines the criteria for selecting a subset of pods
    /// based on their labels. This property is critical for associating the StatefulSet
    /// with the specified pods, ensuring that the StatefulSet operates on the desired group
    /// of resources.
    /// The associated <see cref="LabelSelectorV1"/> allows for both exact matching of labels
    /// and advanced filtering through label selector expressions.
    /// </remarks>
    [YamlMember(Alias = "selector")]
    public LabelSelectorV1 Selector { get; set; } = new();

    /// <summary>
    /// Gets or sets the minimum number of seconds for which a newly created Pod should be ready
    /// without any containers crashing, for it to be considered available. This is used to ensure
    /// that the application is stable before being declared available in a StatefulSet deployment.
    /// If not specified, the default behavior verifies readiness immediately upon initial readiness.
    /// </summary>
    [YamlMember(Alias = "minReadySeconds")]
    public int? MinReadySeconds { get; set; }

    /// <summary>
    /// Configures the ordinals for the StatefulSet in Kubernetes.
    /// </summary>
    /// <remarks>
    /// This property defines the ordinal information for a StatefulSet, which includes settings
    /// such as the starting ordinal for the replicas. Ordinals determine how the StatefulSet
    /// names its pods (e.g., pod-0, pod-1) and offers customization for replica numbering.
    /// </remarks>
    [YamlMember(Alias = "ordinals")]
    public StatefulSetOrdinalsV1? Ordinals { get; set; }

    /// <summary>
    /// Gets or sets the desired number of replicas for the StatefulSet.
    /// This property represents the number of pod instances that should
    /// be maintained by the StatefulSet controller. If not specified,
    /// the default value is 1. A value of null indicates that the field
    /// is not set and the controller will fallback to the default behavior.
    /// </summary>
    [YamlMember(Alias = "replicas")]
    public int? Replicas { get; set; }

    /// <summary>
    /// Gets the list of PersistentVolumeClaim templates used to provision volumes for the StatefulSet.
    /// </summary>
    /// <remarks>
    /// VolumeClaimTemplates define the specifications for persistent volume claims needed by each pod in the StatefulSet.
    /// Each template is used to create a separate PersistentVolumeClaim for every replica managed by the StatefulSet.
    /// The number of claims is determined by the number of replicas defined in the StatefulSet.
    /// </remarks>
    [YamlMember(Alias = "volumeClaimTemplates")]
    public List<PersistentVolumeClaim> VolumeClaimTemplates { get; } = [];

    /// <summary>
    /// Defines the maximum number of revisions of a StatefulSet that will be retained in its history.
    /// When specified, old revisions exceeding this limit are deleted, allowing for storage optimization
    /// while retaining recent history revisions for rollback purposes. If not specified, a default value
    /// determined by the system may be used.
    /// </summary>
    [YamlMember(Alias = "revisionHistoryLimit")]
    public int? RevisionHistoryLimit { get; set; }

    /// <summary>
    /// Describes the retention policy for PersistentVolumeClaims associated with a StatefulSet.
    /// </summary>
    /// <remarks>
    /// Defines how PersistentVolumeClaims should be retained or deleted when the StatefulSet is deleted or scaled down.
    /// Defaults to <see langword="null"/> so the property is omitted from the serialized YAML when no
    /// retention policy has been configured. Eagerly initializing this to <c>new()</c> would otherwise
    /// emit an invalid empty <c>persistentVolumeClaimRetentionPolicy: {}</c> mapping that Kubernetes
    /// rejects on apply.
    /// </remarks>
    [YamlMember(Alias = "persistentVolumeClaimRetentionPolicy")]
    public StatefulSetPersistentVolumeClaimRetentionPolicyV1? PersistentVolumeClaimRetentionPolicy { get; set; }

    /// <summary>
    /// Defines the policy for managing the pods in a StatefulSet.
    /// This property determines how the pods are created, deleted, or updated within the StatefulSet.
    /// Typically, the pod management policy can take values such as "OrderedReady" or "Parallel".
    /// </summary>
    [YamlMember(Alias = "podManagementPolicy")]
    public string? PodManagementPolicy { get; set; }

    /// <summary>
    /// Gets or sets the strategy used to update the stateful set's pods.
    /// This property specifies how updates to the StatefulSet should be performed.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="null"/> so the property is omitted from the serialized YAML when no
    /// update strategy has been configured. Eagerly initializing this to <c>new()</c> would otherwise
    /// emit an invalid empty <c>updateStrategy: {}</c> mapping that Kubernetes rejects on apply.
    /// </remarks>
    [YamlMember(Alias = "updateStrategy")]
    public StatefulSetUpdateStrategyV1? UpdateStrategy { get; set; }
}
