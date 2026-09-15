// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents a single recipe entry inside a <c>Radius.Core/recipePacks</c> recipe pack.
/// </summary>
/// <remarks>
/// Radius 0.60 renamed the emitted schema keys from <c>recipeKind</c> / <c>recipeLocation</c>
/// to <c>kind</c> / <c>source</c> (radius-project/radius#12104). The C# member names are kept
/// as <see cref="RecipeKind"/> / <see cref="RecipeLocation"/> because this type is public;
/// only the emitted property paths changed. Unknown fields are silently dropped by the Radius
/// API server, so emitting the pre-0.60 key names produces an empty recipe pack with no
/// publish-time or deploy-time error — every backing resource then fails recipe resolution.
/// </remarks>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RecipeEntryConstruct : ProvisionableConstruct
{
    private BicepValue<string>? _recipeKind;
    private BicepValue<string>? _recipeLocation;
    private BicepDictionary<object>? _parameters;

    /// <summary>The recipe kind (e.g., "bicep"). Emitted as <c>kind</c>.</summary>
    public BicepValue<string> RecipeKind
    {
        get { Initialize(); return _recipeKind!; }
        set { Initialize(); _recipeKind!.Assign(value); }
    }

    /// <summary>The recipe location (e.g., OCI registry path). Emitted as <c>source</c>.</summary>
    public BicepValue<string> RecipeLocation
    {
        get { Initialize(); return _recipeLocation!; }
        set { Initialize(); _recipeLocation!.Assign(value); }
    }

    /// <summary>
    /// Optional recipe parameters for this entry. Populated only when the
    /// environment declares recipe parameters; left unassigned
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
        _recipeKind = DefineProperty<string>(nameof(RecipeKind), ["kind"]);
        _recipeLocation = DefineProperty<string>(nameof(RecipeLocation), ["source"]);
        _parameters = DefineDictionaryProperty<object>(nameof(Parameters), ["parameters"]);
    }
}
