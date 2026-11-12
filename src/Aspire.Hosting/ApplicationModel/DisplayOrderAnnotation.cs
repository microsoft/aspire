// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// The default display order of a resource in lists of resources. Higher values appear first, consistent with
/// <see cref="ResourceUrlAnnotation.DisplayOrder"/>. Resources without a display order are treated as having an order of 0.
/// </summary>
/// <param name="order">The display order.</param>
[Experimental(ResourceTraitDiagnostics.DiagnosticId, UrlFormat = ResourceTraitDiagnostics.UrlFormat)]
public sealed class DisplayOrderAnnotation(int order) : IResourceAnnotation
{
    /// <summary>Gets the display order. Higher values appear first.</summary>
    public int Order { get; } = order;
}
