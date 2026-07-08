// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a single recipe entry inside a recipe pack (templateKind + templatePath).
/// </summary>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RecipeEntryConstruct : ProvisionableConstruct
{
    private BicepValue<string>? _recipeKind;
    private BicepValue<string>? _recipeLocation;
    private BicepDictionary<object>? _parameters;

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

    /// <summary>
    /// Optional recipe parameters for this entry. Populated only when the
    /// environment declares recipe parameters (FR-004); left unassigned
    /// otherwise so the <c>parameters</c> key is omitted from the emitted Bicep.
    /// </summary>
    public BicepDictionary<object> Parameters
    {
        get { Initialize(); return _parameters!; }
        set { Initialize(); _parameters!.Assign(value); }
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _recipeKind = DefineProperty<string>(nameof(RecipeKind), ["recipeKind"]);
        _recipeLocation = DefineProperty<string>(nameof(RecipeLocation), ["recipeLocation"]);
        _parameters = DefineDictionaryProperty<object>(nameof(Parameters), ["parameters"]);
    }
}
