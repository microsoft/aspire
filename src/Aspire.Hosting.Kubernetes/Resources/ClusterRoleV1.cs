// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a Kubernetes ClusterRole resource in API version v1.
/// </summary>
/// <remarks>
/// A ClusterRole is a cluster-scoped resource in Kubernetes used for Role-Based Access Control (RBAC).
/// It is used to define a set of permissions that are applicable across the entire cluster.
/// </remarks>
[YamlSerializable]
public sealed class ClusterRole() : BaseKubernetesResource("rbac.authorization.k8s.io/v1", "ClusterRole")
{
    /// <summary>
    /// Gets or sets the aggregation rule associated with the ClusterRole.
    /// Defines how multiple cluster roles can be aggregated together, simplifying role-based access control implementation.
    /// </summary>
    /// <remarks>
    /// The AggregationRule allows for automatic composition of permissions by defining
    /// a set of label selectors that identify the roles to be aggregated.
    /// Use this property to configure advanced RBAC mechanisms in Kubernetes clusters.
    /// </remarks>
    [YamlMember(Alias = "aggregationRule", DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public AggregationRuleV1? AggregationRule { get; set; }

    /// <summary>
    /// Represents a collection of policy rules applied to the Kubernetes ClusterRole.
    /// </summary>
    /// <remarks>
    /// The <c>Rules</c> property defines a set of rules that dictate access permissions
    /// within a Kubernetes ClusterRole resource. Each rule describes the actions,
    /// resources, and namespaces that are subject to specific policies.
    /// This property is a list of <c>PolicyRuleV1</c> objects, where each object specifies
    /// the precise components of a policy rule, including API groups, resources, verbs,
    /// and URLs.
    /// </remarks>
    [YamlMember(Alias = "rules", DefaultValuesHandling = DefaultValuesHandling.Preserve)]
    public List<PolicyRuleV1> Rules { get; } = [];
}
