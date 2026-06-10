// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Marks a class as an ATS (Aspire Type System) Data Transfer Object.
/// DTOs are serializable types used to pass structured data to capabilities.
/// </summary>
/// <remarks>
/// <para>
/// DTOs should be simple record or class types with properties that can be
/// serialized to/from JSON. They should not contain complex .NET types like
/// IConfiguration, ILogger, or other framework-specific types.
/// </para>
/// <para>
/// Example:
/// <code>
/// [AspireDto]
/// public sealed class AddRedisOptions {
///     public required string Name { get; init; }
///     public int? Port { get; init; }
/// }
/// </code>
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class AspireDtoAttribute : Attribute
{
    /// <summary>
    /// Gets or sets an optional type identifier for this DTO.
    /// If not specified, the DTO will be serialized as a plain JSON object.
    /// </summary>
    /// <remarks>
    /// When set, the serialized JSON will include a <c>$type</c> field with this value.
    /// </remarks>
    public string? DtoTypeId { get; set; }
}
