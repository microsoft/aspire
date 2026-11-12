// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Diagnostic constants for the experimental resource trait and display-order APIs.
/// </summary>
/// <remarks>
/// These APIs are experimental for 17.0 because trait names are free-form strings and the reserved names that get
/// special handling (such as <c>Aspire_Tag</c>) are not public yet, so the shape may change once well-known traits are designed.
/// The attribute is applied per member rather than on the extension class because <c>[Experimental]</c> on a type is
/// only reported where the type name appears, and extension-method call sites never name the containing class.
/// </remarks>
internal static class ResourceTraitDiagnostics
{
    /// <summary>
    /// Shared diagnostic ID for resource traits, tags, and display order.
    /// </summary>
    public const string DiagnosticId = "ASPIRERESOURCETRAITS001";

    /// <summary>
    /// The documentation link format shared by Aspire's experimental diagnostics.
    /// </summary>
    public const string UrlFormat = "https://aka.ms/aspire/diagnostics/{0}";
}
