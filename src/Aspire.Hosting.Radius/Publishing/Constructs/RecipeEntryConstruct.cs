// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a single recipe entry inside a recipe pack (templateKind + templatePath).
/// </summary>
public sealed class RecipeEntryConstruct : ProvisionableConstruct
{
    private BicepValue<string>? _recipeKind;
    private BicepValue<string>? _recipeLocation;

    /// <summary>The recipe kind (e.g., "bicep").</summary>
    public BicepValue<string> RecipeKind
    {
        get { Initialize(); return _recipeKind!; }
        set { Initialize(); _recipeKind!.Assign(value); }
    }

    /// <summary>The recipe location (e.g., OCI registry path).</summary>
    public BicepValue<string> RecipeLocation
    {
        get { Initialize(); return _recipeLocation!; }
        set { Initialize(); _recipeLocation!.Assign(value); }
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _recipeKind = DefineProperty<string>(nameof(RecipeKind), ["recipeKind"]);
        _recipeLocation = DefineProperty<string>(nameof(RecipeLocation), ["recipeLocation"]);
    }
}
