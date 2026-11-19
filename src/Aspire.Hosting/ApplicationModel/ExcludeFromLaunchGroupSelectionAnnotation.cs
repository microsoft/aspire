// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Preserves a resource's existing startup configuration when a launch group is selected.
/// </summary>
/// <remarks>
/// Use this annotation for support resources that must retain their normal startup behavior independently
/// of launch-group membership. It does not remove an existing <see cref="ExplicitStartupAnnotation"/>.
/// </remarks>
[Experimental("ASPIRELAUNCHGROUPS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ExcludeFromLaunchGroupSelectionAnnotation : IResourceAnnotation
{
}
