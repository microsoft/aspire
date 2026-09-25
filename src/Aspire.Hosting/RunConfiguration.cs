// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>
/// Holds settings applicable to the AppHost run mode (when <see cref="DistributedApplicationExecutionContext.Operation"/>
/// is <see cref="DistributedApplicationOperation.Run"/>).
/// </summary>
/// <remarks>
/// <para>
/// Integrations use it to vary how their resources are launched without changing the core hosting behavior.
/// In <see cref="DistributedApplicationOperation.Publish"/> mode every property holds its default value.
/// </para>
/// </remarks>
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
    /// Indicates that resources should start in watch mode if able.
    /// </summary>
    /// <remarks>
    /// Integrations that support watch can launch their resources so that source changes are hot-reloaded.
    /// This is a hint: integrations that cannot watch their resources should start them in normal fashion.
    /// </remarks>
    public bool WatchEnabled { get; init; }
}
