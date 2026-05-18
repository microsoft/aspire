// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius;

/// <summary>
/// Per-resource configuration for overriding default Radius behavior during publishing.
/// </summary>
[AspireExport(ExposeProperties = true)]
public sealed class RadiusResourceCustomization
{
    /// <summary>
    /// Gets or sets the custom recipe override. When <c>null</c> (default), the resource type instance
    /// omits the <c>recipe:</c> property and Radius selects from the environment's recipe pack.
    /// </summary>
    public RadiusRecipe? Recipe { get; set; }

    /// <summary>
    /// Gets or sets a custom Radius resource type override to bypass the default mapping from
    /// <see cref="Radius.ResourceMapping.ResourceTypeMapper"/>. This enables targeting custom
    /// user-defined types (UDTs) defined by platform engineers, or adopting a new UDT before
    /// the integration ships a built-in mapping.
    /// </summary>
    /// <example>
    /// <code>
    /// cache.PublishAsRadiusResource(r =&gt;
    /// {
    ///     r.TypeOverride = new RadiusResourceTypeReference(
    ///         "MyOrg.Custom/myRedis",
    ///         "2025-01-01");
    /// });
    /// </code>
    /// </example>
    public RadiusResourceTypeReference? TypeOverride { get; set; }
}
