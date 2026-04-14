// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a <c>Radius.Core/environments</c> resource in the Bicep AST.
/// </summary>
public sealed class RadiusEnvironmentConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepList<string>? _recipePacks;
    private BicepValue<string>? _kubernetesNamespace;

    /// <summary>The resource name.</summary>
    public BicepValue<string> EnvironmentName
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    /// <summary>List of recipe pack resource ID references (BicepExpressions).</summary>
    public BicepList<string> RecipePacks
    {
        get { Initialize(); return _recipePacks!; }
        set { Initialize(); _recipePacks!.Assign(value); }
    }

    /// <summary>
    /// Kubernetes namespace for the environment's Kubernetes provider. When set,
    /// emits <c>properties.providers.kubernetes.namespace</c>, which Radius
    /// requires to route built-in compute UDTs (e.g. <c>Radius.Compute/containers</c>)
    /// to a Kubernetes cluster instead of falling back to the Azure provider.
    /// </summary>
    public BicepValue<string> KubernetesNamespace
    {
        get { Initialize(); return _kubernetesNamespace!; }
        set { Initialize(); _kubernetesNamespace!.Assign(value); }
    }

    /// <summary>Initializes a new <see cref="RadiusEnvironmentConstruct"/> with the given Bicep identifier.</summary>
    public RadiusEnvironmentConstruct(string bicepIdentifier)
        : base(bicepIdentifier, new Azure.Core.ResourceType("Radius.Core/environments"), "2025-08-01-preview")
    {
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(EnvironmentName), ["name"]);
        _recipePacks = DefineListProperty<string>(nameof(RecipePacks), ["properties", "recipePacks"]);
        _kubernetesNamespace = DefineProperty<string>(
            nameof(KubernetesNamespace),
            ["properties", "providers", "kubernetes", "namespace"]);
    }
}
