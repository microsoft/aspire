// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a <c>Radius.Core/environments</c> resource in the Bicep AST.
/// </summary>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RadiusEnvironmentConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepList<string>? _recipePacks;
    private BicepValue<string>? _kubernetesNamespace;
    private BicepValue<string>? _azureSubscriptionId;
    private BicepValue<string>? _azureResourceGroupName;
    private BicepValue<string>? _awsAccountId;
    private BicepValue<string>? _awsRegion;

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

    /// <summary>
    /// Subscription GUID of the Azure cloud provider. Emitted as
    /// <c>properties.providers.azure.subscriptionId</c>. The native
    /// <c>Radius.Core/environments</c> schema models the Azure provider with
    /// discrete <c>subscriptionId</c>/<c>resourceGroupName</c> fields (unlike
    /// the legacy <c>Applications.Core/environments</c> single <c>scope</c>
    /// path). Unset for environments that do not configure an Azure provider.
    /// </summary>
    public BicepValue<string> AzureSubscriptionId
    {
        get { Initialize(); return _azureSubscriptionId!; }
        set { Initialize(); _azureSubscriptionId!.Assign(value); }
    }

    /// <summary>
    /// Resource-group name of the Azure cloud provider. Emitted as
    /// <c>properties.providers.azure.resourceGroupName</c>. Unset for
    /// environments that do not configure an Azure provider.
    /// </summary>
    public BicepValue<string> AzureResourceGroupName
    {
        get { Initialize(); return _azureResourceGroupName!; }
        set { Initialize(); _azureResourceGroupName!.Assign(value); }
    }

    /// <summary>
    /// 12-digit account ID of the AWS cloud provider. Emitted as
    /// <c>properties.providers.aws.accountId</c>. The native
    /// <c>Radius.Core/environments</c> schema models the AWS provider with
    /// discrete <c>accountId</c>/<c>region</c> fields (unlike the legacy
    /// <c>Applications.Core/environments</c> single <c>scope</c> path). Unset
    /// for environments that do not configure an AWS provider.
    /// </summary>
    public BicepValue<string> AwsAccountId
    {
        get { Initialize(); return _awsAccountId!; }
        set { Initialize(); _awsAccountId!.Assign(value); }
    }

    /// <summary>
    /// Region code of the AWS cloud provider, e.g. <c>us-west-2</c>. Emitted as
    /// <c>properties.providers.aws.region</c>. Unset for environments that do
    /// not configure an AWS provider.
    /// </summary>
    public BicepValue<string> AwsRegion
    {
        get { Initialize(); return _awsRegion!; }
        set { Initialize(); _awsRegion!.Assign(value); }
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
        _azureSubscriptionId = DefineProperty<string>(
            nameof(AzureSubscriptionId),
            ["properties", "providers", "azure", "subscriptionId"]);
        _azureResourceGroupName = DefineProperty<string>(
            nameof(AzureResourceGroupName),
            ["properties", "providers", "azure", "resourceGroupName"]);
        _awsAccountId = DefineProperty<string>(
            nameof(AwsAccountId),
            ["properties", "providers", "aws", "accountId"]);
        _awsRegion = DefineProperty<string>(
            nameof(AwsRegion),
            ["properties", "providers", "aws", "region"]);
    }
}
