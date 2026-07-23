// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>
/// Describes the run sub-mode the AppHost is running under (when
/// <see cref="DistributedApplicationExecutionContext.Operation"/> is
/// <see cref="DistributedApplicationOperation.Run"/>).
/// </summary>
/// <remarks>
/// The run sub-mode is populated from configuration by the AppHost builder and surfaced through
/// <see cref="DistributedApplicationExecutionContext.RunSubMode"/>. 
/// It lets integrations vary how their resources are launched without changing the core hosting behavior. 
/// In <see cref="DistributedApplicationOperation.Publish"/> mode the sub-mode is always <see cref="Normal"/>.
/// </remarks>
[Experimental("ASPIREWATCH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum RunSubMode
{
    /// <summary>
    /// The AppHost is running normally. Resources are launched using their standard run behavior.
    /// </summary>
    Normal,

    /// <summary>
    /// The AppHost is running in watch sub-mode. Integrations that support watch 
    /// can launch their resources so that source changes are hot-reloaded.
    /// </summary>
    Watch,
}
