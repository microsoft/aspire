// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
public sealed class LegacyApplicationEnvironmentConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepValue<string>? _computeKind;
    private BicepValue<string>? _computeNamespace;
    private BicepDictionary<BicepDictionary<LegacyRecipeEntryConstruct>>? _recipes;

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
    /// Inline recipes keyed by resource type, with each value being a map of
    /// recipe name to recipe entry.
    /// </summary>
    public BicepDictionary<BicepDictionary<LegacyRecipeEntryConstruct>> Recipes
    {
        get { Initialize(); return _recipes!; }
        set { Initialize(); _recipes!.Assign(value); }
    }

    /// <summary>Initializes a new <see cref="LegacyApplicationEnvironmentConstruct"/> with the given Bicep identifier.</summary>
    public LegacyApplicationEnvironmentConstruct(string bicepIdentifier)
        : base(bicepIdentifier, new Azure.Core.ResourceType("Applications.Core/environments"), "2023-10-01-preview")
    {
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(EnvironmentName), ["name"]);
        _computeKind = DefineProperty<string>(nameof(ComputeKind), ["properties", "compute", "kind"]);
        _computeNamespace = DefineProperty<string>(nameof(ComputeNamespace), ["properties", "compute", "namespace"]);
        _recipes = DefineDictionaryProperty<BicepDictionary<LegacyRecipeEntryConstruct>>(
            nameof(Recipes), ["properties", "recipes"]);
    }
}
