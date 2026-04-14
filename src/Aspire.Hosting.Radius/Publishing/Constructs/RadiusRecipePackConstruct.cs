// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a <c>Radius.Core/recipePacks</c> resource in the Bicep AST.
/// </summary>
public sealed class RadiusRecipePackConstruct : ProvisionableResource
{
    private BicepValue<string>? _name;
    private BicepDictionary<RecipeEntryConstruct>? _recipes;

    /// <summary>The resource name.</summary>
    public BicepValue<string> PackName
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }

    /// <summary>
    /// Dictionary mapping resource type strings to recipe entry constructs.
    /// Keys are Radius resource types (e.g., "Radius.Data/redisCaches").
    /// </summary>
    public BicepDictionary<RecipeEntryConstruct> Recipes
    {
        get { Initialize(); return _recipes!; }
        set { Initialize(); _recipes!.Assign(value); }
    }

    /// <summary>Initializes a new <see cref="RadiusRecipePackConstruct"/> with the given Bicep identifier.</summary>
    public RadiusRecipePackConstruct(string bicepIdentifier)
        : base(bicepIdentifier, new Azure.Core.ResourceType("Radius.Core/recipePacks"), "2025-08-01-preview")
    {
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(PackName), ["name"]);
        _recipes = DefineDictionaryProperty<RecipeEntryConstruct>(nameof(Recipes), ["properties", "recipes"]);
    }
}
