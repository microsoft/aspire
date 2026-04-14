// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a TCP/UDP port that a container can expose.
/// </summary>
[DebuggerDisplay("{ValueExpression}")]
public class ContainerPortReference(IResource resource) : IManifestExpressionProvider, IValueWithReferences, IValueProvider
{
    /// <summary>
    /// Gets the resource that this container port is associated with.
    /// </summary>
    public IResource Resource { get; } = resource;

    /// <inheritdoc/>
    public string ValueExpression => $"{{{Resource.Name}.containerPort}}";

    /// <inheritdoc/>
    [global::Aspire.Hosting.AspireExportIgnore(Reason = "Reference enumeration is not needed in the ATS surface for container port provenance.")]
    public IEnumerable<object> References => [Resource];

    ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<string?>("8080");
}
