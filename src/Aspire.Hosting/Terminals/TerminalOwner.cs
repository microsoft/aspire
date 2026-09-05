// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Identifies which process owns a terminal's workload, and therefore controls its lifetime.
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
    /// The workload runs in the AppHost process itself, and its lifetime is controlled by whoever created it.
    /// </summary>
    AppHost,

    /// <summary>
    /// The workload belongs to a resource in the application model, and its lifetime follows that resource.
    /// </summary>
    /// <remarks>
    /// These terminals run out-of-process in a per-replica terminal host rather than in the AppHost, so
    /// disposing the <see cref="IAspireTerminal"/> releases Aspire's handle on the terminal without stopping
    /// the underlying workload.
    /// </remarks>
    Resource
}
