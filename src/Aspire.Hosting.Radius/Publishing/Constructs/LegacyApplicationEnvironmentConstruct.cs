// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Radius.ResourceMapping;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a legacy <c>Applications.Core/environments@2023-10-01-preview</c>
/// resource in the Bicep AST. Used as a parent for <c>Applications.*</c> portable
/// resources whose UDT counterparts are not yet GA (Redis, Mongo, RabbitMQ,
/// Dapr state store, Dapr pubsub).
/// </summary>
/// <remarks>
/// Legacy environments carry recipes inline under <c>properties.recipes</c>
/// (nested <c>type → recipeName → { templateKind, templatePath }</c>) — the
/// legacy schema keeps the original key names. The new <c>recipeKind</c> /
/// <c>recipeLocation</c> keys are only used by <c>Radius.Core/recipePacks</c>.
/// </remarks>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExport]
public sealed class LegacyApplicationEnvironmentConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _computeKind;
    private BicepValue<string>? _computeNamespace;
    private BicepValue<string>? _azureScope;
    private BicepValue<string>? _awsScope;
    private BicepDictionary<BicepDictionary<LegacyRecipeEntryConstruct>>? _recipes;
    private BicepDictionary<object>? _recipeConfig;

    /// <summary>The resource name.</summary>
    public BicepValue<string> EnvironmentName
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    /// <summary>Compute kind (e.g., <c>kubernetes</c>).</summary>
    public BicepValue<string> ComputeKind
    {
        get { Initialize(); return _computeKind!; }
        set { Initialize(); _computeKind!.Assign(value); }
    }

    /// <summary>Compute namespace for Kubernetes.</summary>
    public BicepValue<string> ComputeNamespace
    {
        get { Initialize(); return _computeNamespace!; }
        set { Initialize(); _computeNamespace!.Assign(value); }
    }

    /// <summary>
    /// Scope path of the Azure cloud provider, emitted as
    /// <c>properties.providers.azure.scope</c>. Unset for environments that do not
    /// configure an Azure provider. Required when a cloud-managed resource on this
    /// environment is materialized via a legacy <c>Applications.*</c> recipe so Radius
    /// can resolve the Azure provider during deployment.
    /// </summary>
    public BicepValue<string> AzureScope
    {
        get { Initialize(); return _azureScope!; }
        set { Initialize(); _azureScope!.Assign(value); }
    }

    /// <summary>
    /// Scope path of the AWS cloud provider, emitted as
    /// <c>properties.providers.aws.scope</c>. Unset for environments that do not
    /// configure an AWS provider.
    /// </summary>
    public BicepValue<string> AwsScope
    {
        get { Initialize(); return _awsScope!; }
        set { Initialize(); _awsScope!.Assign(value); }
    }

    /// <summary>
    /// Inline recipes keyed by resource type, with each value being a map of
    /// recipe name to recipe entry.
    /// </summary>
    public BicepDictionary<BicepDictionary<LegacyRecipeEntryConstruct>> Recipes
    {
        get { Initialize(); return _recipes!; }
        set { Initialize(); _recipes!.Assign(value); }
    }

    /// <summary>
    /// The <c>recipeConfig</c> block (<c>properties.recipeConfig</c>) carrying secret-store
    /// references for private Bicep-registry auth, Terraform Git PAT auth, and
    /// <c>envSecrets</c>. Populated only when a secret store is consumed.
    /// </summary>
    public BicepDictionary<object> RecipeConfig
    {
        get { Initialize(); return _recipeConfig!; }
        set { Initialize(); _recipeConfig!.Assign(value); }
    }

    /// <summary>Initializes a new <see cref="LegacyApplicationEnvironmentConstruct"/> with the given Bicep identifier.</summary>
    public LegacyApplicationEnvironmentConstruct(string bicepIdentifier)
        : base(bicepIdentifier, new Azure.Core.ResourceType(RadiusResourceTypes.LegacyEnvironments), RadiusResourceTypes.LegacyApiVersion)
    {
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(EnvironmentName), ["name"]);
        _computeKind = DefineProperty<string>(nameof(ComputeKind), ["properties", "compute", "kind"]);
        _computeNamespace = DefineProperty<string>(nameof(ComputeNamespace), ["properties", "compute", "namespace"]);
        _azureScope = DefineProperty<string>(nameof(AzureScope), ["properties", "providers", "azure", "scope"]);
        _awsScope = DefineProperty<string>(nameof(AwsScope), ["properties", "providers", "aws", "scope"]);
        _recipes = DefineDictionaryProperty<BicepDictionary<LegacyRecipeEntryConstruct>>(
            nameof(Recipes), ["properties", "recipes"]);
        _recipeConfig = DefineDictionaryProperty<object>(nameof(RecipeConfig), ["properties", "recipeConfig"]);
    }
}
