// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Identifies a resource whose model shape cannot participate in resource projections.
/// </summary>
/// <remarks>
/// Implement this interface on resources whose type is intrinsic to their model identity, such as parameters.
/// Such a resource cannot be used as either the canonical owner or the effective target of a projection.
/// Resources are otherwise projectable by default, subject to the source and target shape compatibility rules.
/// </remarks>
[Experimental("ASPIREPROJECTIONS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireExportIgnore(Reason = "Projection eligibility is a .NET authoring contract with no polyglot runtime surface.")]
public interface IResourceWithoutProjections : IResource
{
}
