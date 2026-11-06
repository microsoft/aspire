// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Records whether a .NET project resource takes part in <c>aspire run --watch</c> hot reload.
/// </summary>
/// <remarks>
/// <para>
/// Add this annotation with <see cref="DotnetProjectHostingExtensions.WithWatch(IResourceBuilder{DotnetProjectResource}, bool)"/>.
/// Without it, every .NET project resource is watched in an <c>aspire run --watch</c> run unless it runs to completion
/// (another resource waits for it with <c>WaitForCompletion</c>).
/// </para>
/// <para>
/// Integrations that replace a project's launch with their own tool, which the watcher can't launch, add this annotation
/// with <see cref="Enabled"/> set to <see langword="false"/> so that the resource runs without hot reload rather than
/// failing the watch run.
/// </para>
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, Enabled = {Enabled}")]
public sealed class DotnetProjectWatchAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DotnetProjectWatchAnnotation"/> class.
    /// </summary>
    /// <param name="enabled">
    /// <see langword="true"/> to require hot reload for the resource in watch runs; <see langword="false"/> to run it
    /// without hot reload.
    /// </param>
    public DotnetProjectWatchAnnotation(bool enabled)
    {
        Enabled = enabled;
    }

    /// <summary>
    /// Gets a value indicating whether the resource is watched in an <c>aspire run --watch</c> run.
    /// </summary>
    /// <remarks>
    /// <see langword="true"/> explicitly requires hot reload: a resource that can't be watched then fails the run instead
    /// of being excluded automatically. <see langword="false"/> runs the resource without hot reload.
    /// </remarks>
    public bool Enabled { get; }
}
