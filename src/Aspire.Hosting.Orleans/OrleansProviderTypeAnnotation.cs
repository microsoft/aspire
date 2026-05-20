// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Orleans;

/// <summary>
/// Annotation to specify the Orleans provider type for a resource.
/// </summary>
public sealed class OrleansProviderTypeAnnotation : IResourceAnnotation
{
    /// <summary>
    /// The provider type of the resource that should be used instead of the default.
    /// </summary>
    public string ProviderType { get; internal set; } = "";
}
