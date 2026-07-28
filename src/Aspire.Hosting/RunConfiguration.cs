// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>
/// Describes how the AppHost is being run when <see cref="DistributedApplicationExecutionContext.Operation"/>
/// is <see cref="DistributedApplicationOperation.Run"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each property describes an independent aspect of the run, so additional run behaviors can be introduced
/// over time without collapsing them into a single mutually exclusive mode.
/// </para>
/// <para>
/// Integrations use it to vary how their resources are launched without changing the core hosting behavior.
/// In <see cref="DistributedApplicationOperation.Publish"/> mode every property holds its default value.
/// </para>
/// </remarks>
/// <example>
/// This example launches a resource differently when the AppHost is running in watch mode:
/// <code>
/// if (builder.ExecutionContext.RunConfiguration.WatchEnabled)
/// {
///     // Launch the resource so that source changes are hot-reloaded.
/// }
/// </code>
/// </example>
[Experimental("ASPIREWATCH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[AspireDto]
public sealed class RunConfiguration
{
    /// <summary>
    /// A configuration where every aspect of the run holds its default value.
    /// </summary>
    /// <remarks>
    /// Shared rather than allocated per context because the instance is immutable.
    /// </remarks>
    internal static RunConfiguration Default { get; } = new();

    /// <summary>
    /// Indicates that the AppHost was started in watch mode.
    /// </summary>
    /// <remarks>
    /// Integrations that support watch can launch their resources so that source changes are hot-reloaded.
    /// This is a hint: integrations that cannot watch their resources are free to ignore it.
    /// </remarks>
    public bool WatchEnabled { get; init; }
}
