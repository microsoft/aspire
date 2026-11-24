// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Records whether a .NET project resource is watched for hot reload, overriding the AppHost's resource-watch default.
/// </summary>
/// <remarks>
/// <para>
/// Add this annotation with <see cref="DotnetProjectHostingExtensions.WithWatch(IResourceBuilder{DotnetProjectResource}, bool)"/>.
/// Without it, the AppHost's resource-watch default applies: compatible .NET project resources are watched in Run mode
/// unless <see cref="DistributedApplicationOptions.DefaultResourceWatchEnabled"/> or <c>AppHost:Run:WatchEnabled</c>
/// turns watching off. The CLI's <c>--watch</c> option controls AppHost edits only.
/// </para>
/// <para>
/// A project that can't be watched runs without hot reload and says so: for example, one with a persistent lifetime or
/// a custom launch tool, or one that another resource waits for with <c>WaitForCompletion</c>. With <see cref="Enabled"/>
/// set to <see langword="true"/>, such a project fails the run instead.
/// </para>
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, Enabled = {Enabled}")]
public sealed class DotnetProjectWatchAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DotnetProjectWatchAnnotation"/> class.
    /// </summary>
    /// <param name="enabled">
    /// <see langword="true"/> to require hot reload for the resource; <see langword="false"/> to run it without hot reload.
    /// </param>
    public DotnetProjectWatchAnnotation(bool enabled)
    {
        Enabled = enabled;
    }

    /// <summary>
    /// Gets a value indicating whether the resource is watched for hot reload.
    /// </summary>
    /// <remarks>
    /// <see langword="true"/> explicitly requires hot reload: a resource that can't be watched then fails the run instead
    /// of being excluded automatically. <see langword="false"/> runs the resource without hot reload.
    /// </remarks>
    public bool Enabled { get; }
}
