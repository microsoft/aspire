// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Radius;

/// <summary>
/// Represents a custom Radius recipe override for a resource.
/// </summary>
/// <remarks>
/// <para>
/// <b>Upstream deprecation note</b>: Radius's user-defined resource type (UDT) model is
/// retiring named recipes in favor of one recipe per resource type per environment.
/// <see cref="Name"/> will continue to work with legacy portable resource types but should
/// be considered deprecated for forward compatibility. <see cref="RecipeLocation"/> and
/// <see cref="Parameters"/> remain valid under UDTs.
/// </para>
/// <para>
/// See: https://github.com/radius-project/radius/blob/main/eng/design-notes/extensibility/2025-02-user-defined-resource-type-feature-spec.md
/// </para>
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class RadiusRecipe
{
    /// <summary>
    /// Gets or sets the recipe name to select for this resource.
    /// </summary>
    /// <remarks>
    /// <b>Upstream deprecation note</b>: Radius's UDT model is retiring named recipe selection.
    /// Under UDTs, each resource type has one recipe per environment — developers do not select
    /// recipes by name. This property will continue to function during the transition period
    /// (which is why it is marked <see cref="ExperimentalAttribute"/> rather than
    /// <see cref="ObsoleteAttribute"/>), and will be marked <see cref="ObsoleteAttribute"/> and
    /// eventually removed once the UDT model is broadly available. The default is the empty
    /// string, which the publisher treats as <c>"default"</c> when emitting the recipe pack.
    /// </remarks>
    /// <remarks>
    /// Not marked <c>required</c> — CS9042 disallows combining <c>required</c> with the
    /// <see cref="ExperimentalAttribute"/> (which the compiler treats like
    /// <see cref="ObsoleteAttribute"/>) unless the containing type's constructors are all
    /// experimental too. The reviewer specifically wanted only this property — not the
    /// whole type — to carry the experimental signal.
    /// </remarks>
    [Experimental("ASPIRERADIUS002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OCI template location for recipe registration in a recipe pack.
    /// </summary>
    /// <remarks>
    /// Emitted as <c>recipeLocation</c> on the new <c>Radius.Core/recipePacks</c> UDT, and as
    /// <c>templatePath</c> under the legacy <c>Applications.Core/environments@2023-10-01-preview</c>
    /// inline <c>properties.recipes</c> shape. Renamed from <c>TemplatePath</c> in
    /// 2026-04-20 to align with the shipped Radius UDT schema (breaking pre-GA change).
    /// </remarks>
    public string? RecipeLocation { get; set; }

    /// <summary>
    /// Gets the recipe parameters. Values are serialized to correct Bicep types
    /// (e.g., <c>42</c> not <c>"42"</c>, <c>true</c> not <c>"true"</c>).
    /// </summary>
    public IDictionary<string, object> Parameters { get; } = new Dictionary<string, object>();
}
