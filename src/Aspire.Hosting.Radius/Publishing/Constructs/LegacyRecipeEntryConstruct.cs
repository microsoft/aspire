// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Radius.Publishing.Constructs;

/// <summary>
/// Represents an individual recipe entry nested inside a legacy
/// <c>Applications.Core/environments</c> <c>properties.recipes</c> block.
/// Uses the original legacy schema keys <c>templateKind</c> /
/// <c>templatePath</c> (the new <c>recipeKind</c> / <c>recipeLocation</c> keys
/// are only valid on <c>Radius.Core/recipePacks</c>).
/// </summary>
public sealed class LegacyRecipeEntryConstruct : ProvisionableConstruct
{
    private BicepValue<string>? _templateKind;
    private BicepValue<string>? _templatePath;

    /// <summary>Recipe template kind (e.g., "bicep").</summary>
    public BicepValue<string> TemplateKind
    {
        get { Initialize(); return _templateKind!; }
        set { Initialize(); _templateKind!.Assign(value); }
    }

    /// <summary>Recipe template path / OCI registry URL.</summary>
    public BicepValue<string> TemplatePath
    {
        get { Initialize(); return _templatePath!; }
        set { Initialize(); _templatePath!.Assign(value); }
    }

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _templateKind = DefineProperty<string>(nameof(TemplateKind), ["templateKind"]);
        _templatePath = DefineProperty<string>(nameof(TemplatePath), ["templatePath"]);
    }
}
