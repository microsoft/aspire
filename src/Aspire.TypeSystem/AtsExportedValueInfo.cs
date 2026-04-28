// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.TypeSystem;

/// <summary>
/// Represents an immutable exported ATS value discovered from <c>[AspireValue]</c>.
/// </summary>
public sealed class AtsExportedValueInfo
{
    /// <summary>
    /// Gets the name of the assembly that exported this value.
    /// </summary>
    public required string OwningAssemblyName { get; init; }

    /// <summary>
    /// Gets the full path of the exported value in generated guest SDKs.
    /// </summary>
    /// <remarks>
    /// The first segment is the generated catalog root name.
    /// </remarks>
    public required IReadOnlyList<string> PathSegments { get; init; }

    /// <summary>
    /// Gets the snapped JSON value emitted into guest SDKs.
    /// </summary>
    public required JsonNode? Value { get; init; }

    /// <summary>
    /// Gets the ATS type of the exported value.
    /// </summary>
    public required AtsTypeRef Type { get; init; }

    /// <summary>
    /// Gets an optional XML documentation summary for the exported value.
    /// </summary>
    public string? Description { get; init; }
}
