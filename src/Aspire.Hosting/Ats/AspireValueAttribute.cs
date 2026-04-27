// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>
/// Marks an immutable static field or property as an ATS-exported value.
/// </summary>
/// <remarks>
/// <para>
/// Exported values are snapped during ATS scanning and emitted into generated guest SDKs as
/// predefined values. They are intended for immutable DTO, enum, and primitive values that
/// should be available without re-declaring them in each guest language.
/// </para>
/// <para>
/// The <paramref name="catalogName"/> becomes the root name of the generated value catalog in
/// guest SDKs. Nested static types are emitted as nested namespaces or classes under that root.
/// </para>
/// <para>
/// Exported values should be side-effect free and should not return handles or other runtime-bound
/// ATS types.
/// </para>
/// </remarks>
/// <param name="catalogName">The root name of the generated value catalog.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
[Experimental("ASPIREATS001")]
public sealed class AspireValueAttribute(string catalogName) : Attribute
{
    /// <summary>
    /// Gets the root name of the generated value catalog.
    /// </summary>
    public string CatalogName { get; } = !string.IsNullOrWhiteSpace(catalogName)
        ? catalogName
        : throw new ArgumentException("Catalog name cannot be null or whitespace.", nameof(catalogName));

    /// <summary>
    /// Gets or sets an optional override for the exported value name.
    /// </summary>
    public string? Name { get; set; }
}
