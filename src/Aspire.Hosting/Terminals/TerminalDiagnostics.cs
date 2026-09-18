// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Diagnostic metadata for the experimental terminal APIs.
/// </summary>
internal static class TerminalDiagnostics
{
    /// <summary>
    /// Shared diagnostic ID for resource terminals, AppHost-owned terminals, and terminal interactions.
    /// </summary>
    public const string DiagnosticId = "ASPIRETERMINAL001";

    /// <summary>
    /// The documentation link format shared by Aspire's experimental diagnostics.
    /// </summary>
    public const string UrlFormat = "https://aka.ms/aspire/diagnostics/{0}";
}
