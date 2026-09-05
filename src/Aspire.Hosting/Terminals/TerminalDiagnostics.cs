// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Diagnostic ids for the experimental AppHost-owned terminal API.
/// </summary>
internal static class TerminalDiagnostics
{
    /// <summary>
    /// Terminals owned by the AppHost process — <see cref="TerminalService"/>, <see cref="IAspireTerminal"/>
    /// and the types they take.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>ASPIRETERMINAL001</c>, which covers <c>WithTerminal</c> — terminals for DCP-owned
    /// resource processes. The two are separate features with separate lifetimes and separate transports, so
    /// suppressing one should not silently opt into the other.
    /// </remarks>
    public const string AppHostTerminals = "ASPIRETERMINAL002";

    /// <summary>
    /// The documentation link format shared by Aspire's experimental diagnostics.
    /// </summary>
    public const string UrlFormat = "https://aka.ms/aspire/diagnostics/{0}";
}
