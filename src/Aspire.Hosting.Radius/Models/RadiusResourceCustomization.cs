// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius.Models;

/// <summary>
/// Per-resource configuration for overriding default Radius behavior during publishing.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class RadiusResourceCustomization
{
    /// <summary>
    /// Gets or sets the custom recipe override. When <c>null</c> (default), the resource type instance
    /// omits the <c>recipe:</c> property and Radius selects from the environment's recipe pack.
    /// </summary>
    public RadiusRecipe? Recipe { get; set; }

    /// <summary>
    /// Gets or sets how the resource is provisioned. Defaults to <see cref="ResourceProvisioning.Automatic"/>.
    /// </summary>
    /// <remarks>
    /// Setting <see cref="ResourceProvisioning.Manual"/> with a non-null <see cref="Recipe"/> is invalid
    /// and will produce an error at Bicep generation time.
    /// </remarks>
    public ResourceProvisioning Provisioning { get; set; } = ResourceProvisioning.Automatic;

    /// <summary>
    /// Gets or sets connection string format overrides. Keys are reference names.
    /// </summary>
    public Dictionary<string, string> ConnectionStringOverrides { get; set; } = [];

    /// <summary>
    /// Gets or sets a custom Radius resource type string to override the default mapping from
    /// <see cref="Radius.ResourceMapping.ResourceTypeMapper"/>. This enables targeting custom
    /// user-defined types (UDTs) defined by platform engineers.
    /// </summary>
    /// <example>
    /// <code>
    /// cache.PublishAsRadiusResource(r =&gt;
    /// {
    ///     r.RadiusType = "MyOrg.Custom/myRedis";
    ///     r.RadiusApiVersion = "2025-01-01";
    /// });
    /// </code>
    /// </example>
    public string? RadiusType { get; set; }

    /// <summary>
    /// Gets or sets the API version for the custom Radius resource type specified in <see cref="RadiusType"/>.
    /// Ignored when <see cref="RadiusType"/> is null. Defaults to <c>2025-08-01-preview</c> if not specified.
    /// </summary>
    public string? RadiusApiVersion { get; set; }
}
