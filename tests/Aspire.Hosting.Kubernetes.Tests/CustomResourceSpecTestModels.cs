// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0005
using System.Collections.Generic;
using Aspire.Hosting.Kubernetes.Resources;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Tests;

internal sealed class ConfigMapDataSpec : CustomResourceSpecV1
{
    public ConfigMapDataSpec(object data) => Data = data;

    [YamlMember(Alias = "data")]
    public object Data { get; }
}

internal sealed class SecretDataSpec : CustomResourceSpecV1
{
    public SecretDataSpec(object stringData) => StringData = stringData;

    [YamlMember(Alias = "stringData")]
    public object StringData { get; }
}

internal sealed class EmptyCustomResourceSpec : CustomResourceSpecV1
{
}

internal sealed class StringValueSpec : CustomResourceSpecV1
{
    public StringValueSpec(string value) => Value = value;

    [YamlMember(Alias = "value")]
    public string Value { get; }
}

internal sealed class ArrayValueSpec : CustomResourceSpecV1
{
    public ArrayValueSpec(string[] items) => Items = items;

    [YamlMember(Alias = "items")]
    public string[] Items { get; }
}

internal sealed class GenericObjectSpec : CustomResourceSpecV1
{
    public GenericObjectSpec(object data) => Data = data;

    [YamlMember(Alias = "data")]
    public object Data { get; }
}

internal sealed class SimpleCustomResourceSpec : CustomResourceSpecV1
{
    public SimpleCustomResourceSpec(string name, int version, NestedCustomResourceSpec nested)
    {
        Name = name;
        Version = version;
        Nested = nested;
    }

    [YamlMember(Alias = "name")]
    public string Name { get; }

    [YamlMember(Alias = "version")]
    public int Version { get; }

    [YamlMember(Alias = "nested")]
    public NestedCustomResourceSpec Nested { get; }
}

internal sealed class NestedCustomResourceSpec
{
    public NestedCustomResourceSpec(string value) => Value = value;

    [YamlMember(Alias = "value")]
    public string Value { get; }
}

internal sealed class KubernetesCustomResourceDeploymentSpec : CustomResourceSpecV1
{
    public KubernetesCustomResourceDeploymentSpec(int replicas, KubernetesCustomResourceSelectorSpec selector, KubernetesCustomResourceTemplateSpec template)
    {
        Replicas = replicas;
        Selector = selector;
        Template = template;
    }

    [YamlMember(Alias = "replicas")]
    public int Replicas { get; }

    [YamlMember(Alias = "selector")]
    public KubernetesCustomResourceSelectorSpec Selector { get; }

    [YamlMember(Alias = "template")]
    public KubernetesCustomResourceTemplateSpec Template { get; }
}

internal sealed class KubernetesCustomResourceSelectorSpec
{
    public KubernetesCustomResourceSelectorSpec(Dictionary<string, string> matchLabels) => MatchLabels = matchLabels;

    [YamlMember(Alias = "matchLabels")]
    public Dictionary<string, string> MatchLabels { get; }
}

internal sealed class KubernetesCustomResourceTemplateSpec
{
    public KubernetesCustomResourceTemplateSpec(KubernetesCustomResourceTemplateMetadata metadata, KubernetesCustomResourcePodSpec spec)
    {
        Metadata = metadata;
        Spec = spec;
    }

    [YamlMember(Alias = "metadata")]
    public KubernetesCustomResourceTemplateMetadata Metadata { get; }

    [YamlMember(Alias = "spec")]
    public KubernetesCustomResourcePodSpec Spec { get; }
}

internal sealed class KubernetesCustomResourceTemplateMetadata
{
    public KubernetesCustomResourceTemplateMetadata(Dictionary<string, string> labels) => Labels = labels;

    [YamlMember(Alias = "labels")]
    public Dictionary<string, string> Labels { get; }
}

internal sealed class KubernetesCustomResourcePodSpec
{
    public KubernetesCustomResourcePodSpec(KubernetesCustomResourceContainerSpec[] containers) => Containers = containers;

    [YamlMember(Alias = "containers")]
    public KubernetesCustomResourceContainerSpec[] Containers { get; }
}

internal sealed class KubernetesCustomResourceContainerSpec
{
    public KubernetesCustomResourceContainerSpec(string name, string image)
    {
        Name = name;
        Image = image;
    }

    [YamlMember(Alias = "name")]
    public string Name { get; }

    [YamlMember(Alias = "image")]
    public string Image { get; }
}

internal sealed class CertManagerCustomResourceSpec : CustomResourceSpecV1
{
    public CertManagerCustomResourceSpec(bool enabled, string ns)
    {
        Enabled = enabled;
        Ns = ns;
    }

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; }

    [YamlMember(Alias = "ns")]
    public string Ns { get; }
}

internal sealed class MonitoringRuleCustomResourceSpec : CustomResourceSpecV1
{
    public MonitoringRuleCustomResourceSpec(MonitoringRuleGroupSpec[] groups) => Groups = groups;

    [YamlMember(Alias = "groups")]
    public MonitoringRuleGroupSpec[] Groups { get; }
}

internal sealed class MonitoringRuleGroupSpec
{
    public MonitoringRuleGroupSpec(string name) => Name = name;

    [YamlMember(Alias = "name")]
    public string Name { get; }
}
