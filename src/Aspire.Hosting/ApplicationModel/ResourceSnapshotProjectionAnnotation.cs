// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Projects a resource's observed snapshot before it is published.
/// </summary>
/// <remarks>
/// Use this when an executable represents a transport whose state differs from the application it owns.
/// The callback runs synchronously under the resource's notification lock. It must not block, perform
/// I/O, publish another snapshot, or mutate the application model. The last annotation wins.
/// </remarks>
/// <param name="projection">The pure snapshot projection callback.</param>
[Experimental("ASPIREWATCH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ResourceSnapshotProjectionAnnotation(
    Func<string, CustomResourceSnapshot, CustomResourceSnapshot> projection) : IResourceAnnotation
{
    /// <summary>
    /// Gets the callback, whose arguments are the resource instance identifier and proposed snapshot.
    /// </summary>
    public Func<string, CustomResourceSnapshot, CustomResourceSnapshot> Projection { get; } =
        projection ?? throw new ArgumentNullException(nameof(projection));
}
