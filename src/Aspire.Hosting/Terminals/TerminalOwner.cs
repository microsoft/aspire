// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Identifies whether the AppHost or an application resource controls a terminal's workload lifetime.
/// </summary>
/// <remarks>
/// This is fixed when the terminal is created and never changes. It is distinct from
/// <see cref="TerminalPlacement"/>, which describes where the terminal is currently displayed and can change
/// over the terminal's life.
/// </remarks>
[Experimental(TerminalDiagnostics.AppHostTerminals, UrlFormat = TerminalDiagnostics.UrlFormat)]
public enum TerminalOwner
{
    /// <summary>
    /// The terminal is created by AppHost code, and its lifetime is controlled by whoever created it.
    /// </summary>
    /// <remarks>
    /// Commands run as child processes of the AppHost. Ownership does not imply in-process execution.
    /// </remarks>
    AppHost,

    /// <summary>
    /// The workload belongs to a resource in the application model, and its lifetime follows that resource.
    /// </summary>
    /// <remarks>
    /// The resource's workload is orchestrated by DCP and exposed through a per-replica terminal host.
    /// Disposing the <see cref="AspireTerminal"/> releases Aspire's handle on the terminal without stopping
    /// that workload.
    /// </remarks>
    Resource
}
