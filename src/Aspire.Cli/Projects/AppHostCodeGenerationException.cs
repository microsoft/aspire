// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

/// <summary>
/// Thrown by <see cref="AppHostRpcClient"/> when the AppHost server reports a reflection-based
/// code-generation failure. Carries a structured <see cref="AppHostCodeGenerationDiagnostic"/>
/// payload supplied by the server so the CLI can render an actionable, tiered diagnostic.
/// </summary>
/// <remarks>
/// The <see cref="Exception.Message"/> always contains the short, language-agnostic message the
/// server produced; the full structured payload — including .NET-specific identifiers such as
/// type names and assembly identities — is exposed via <see cref="Diagnostic"/> and is only
/// rendered to the user when <c>--debug</c> is supplied.
/// </remarks>
internal sealed class AppHostCodeGenerationException : Exception
{
    public AppHostCodeGenerationException(string message, AppHostCodeGenerationDiagnostic diagnostic, Exception? innerException = null)
        : base(message, innerException)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>
    /// Gets the structured diagnostic payload that accompanied the RPC failure.
    /// </summary>
    public AppHostCodeGenerationDiagnostic Diagnostic { get; }
}
