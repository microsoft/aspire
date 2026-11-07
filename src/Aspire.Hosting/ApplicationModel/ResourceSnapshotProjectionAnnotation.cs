// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Projects a resource snapshot before publication and command-state evaluation.
/// </summary>
/// <remarks>
/// The last annotation wins. The callback runs synchronously under the resource instance's
/// notification lock and must be pure: it must not block, perform I/O, publish recursively,
/// or mutate the application model. It also receives logical updates based on a previously
/// projected snapshot, so the projection must not interpret every update as a new transport observation.
/// </remarks>
/// <param name="projection">The callback receiving the resource instance identifier and proposed snapshot.</param>
[Experimental("ASPIREWATCH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ResourceSnapshotProjectionAnnotation(
    Func<string, CustomResourceSnapshot, CustomResourceSnapshot> projection) : IResourceAnnotation
{
    /// <summary>
    /// Gets the pure snapshot projection callback.
    /// </summary>
    public Func<string, CustomResourceSnapshot, CustomResourceSnapshot> Projection { get; } =
        projection ?? throw new ArgumentNullException(nameof(projection));
}
