// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Decides whether a start of a resource instance waits for the dependencies that the resource declares with
/// <c>WaitFor</c>, <c>WaitForStart</c>, or <c>WaitForCompletion</c>.
/// </summary>
/// <remarks>
/// <para>
/// The last annotation wins. Aspire consults it only when it starts a specific instance of the resource, before it
/// waits for the dependencies. Starts that cover the resource as a whole always wait.
/// </para>
/// <para>
/// An integration that relaunches an instance whose dependencies were satisfied earlier in the run, for example after
/// replacing the hot reload watcher that owns it, uses this annotation so that the relaunch doesn't wait for a
/// dependency that has since finished and doesn't run again. The callback runs before the instance starts and must
/// not block.
/// </para>
/// <para>
/// This is an experimental feature and may change in future releases.
/// </para>
/// </remarks>
/// <param name="shouldWait">
/// The callback that receives the resource instance identifier and returns <see langword="false"/> to start the
/// instance without waiting for its dependencies.
/// </param>
[Experimental("ASPIREWATCH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ResourceDependencyWaitFilterAnnotation(Func<string, bool> shouldWait) : IResourceAnnotation
{
    /// <summary>
    /// Gets the callback that decides whether a start of a resource instance waits for the resource's dependencies.
    /// </summary>
    /// <value>
    /// A callback that receives the resource instance identifier and returns <see langword="true"/> to wait for the
    /// dependencies, or <see langword="false"/> to start the instance without waiting.
    /// </value>
    public Func<string, bool> ShouldWait { get; } = shouldWait ?? throw new ArgumentNullException(nameof(shouldWait));
}
