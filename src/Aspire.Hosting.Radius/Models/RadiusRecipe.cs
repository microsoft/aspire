// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius.Models;

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
public class RadiusRecipe
{
    /// <summary>
    /// Gets or sets the recipe name to select for this resource.
    /// </summary>
    /// <remarks>
    /// <b>Upstream deprecation note</b>: Radius's UDT model is retiring named recipe selection.
    /// Under UDTs, each resource type has one recipe per environment — developers do not select
    /// recipes by name. This property will continue to function during the transition period.
    /// </remarks>
    public required string Name { get; set; }

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
    /// Gets or sets the recipe parameters. Values are serialized to correct Bicep types
    /// (e.g., <c>42</c> not <c>"42"</c>, <c>true</c> not <c>"true"</c>).
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = [];
}
