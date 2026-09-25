// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Persistence modes for resources that support lifetime configuration.
/// </summary>
[Experimental("ASPIREPERSISTENCE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum PersistenceMode
{
    /// <summary>
    /// Create the resource when the app host process starts and dispose of it when the app host process shuts down.
    /// </summary>
    Session,

    /// <summary>
    /// Attempt to re-use a previously created resource if one exists. Do not destroy the resource on app host process shutdown.
    /// </summary>
    Persistent,

    /// <summary>
    /// Match another resource's persistence behavior.
    /// </summary>
    Resource,

    /// <summary>
    /// Use persistent behavior scoped to a parent process identity.
    /// </summary>
    ParentProcess,
}

/// <summary>
/// Annotation that controls the persistence behavior of a resource.
/// </summary>
[Experimental("ASPIREPERSISTENCE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[DebuggerDisplay("Type = {GetType().Name,nq}, Mode = {Mode}")]
public sealed class PersistenceAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the persistence mode.
    /// </summary>
    public required PersistenceMode Mode { get; set; }

    /// <summary>
    /// Gets or sets the source resource when <see cref="Mode"/> is <see cref="PersistenceMode.Resource"/>.
    /// </summary>
    public IResource? SourceResource { get; set; }

    /// <summary>
    /// Gets or sets the parent process ID when <see cref="Mode"/> is <see cref="PersistenceMode.ParentProcess"/>.
    /// </summary>
    public int? ParentProcessId { get; set; }

    /// <summary>
    /// Gets or sets the parent process identity timestamp when <see cref="Mode"/> is <see cref="PersistenceMode.ParentProcess"/>.
    /// </summary>
    public DateTime? ParentProcessTimestamp { get; set; }
}
