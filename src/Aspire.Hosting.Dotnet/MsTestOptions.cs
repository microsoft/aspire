// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Configures the build selection and runner arguments for an MSTest executable project.
/// </summary>
/// <remarks>Only one target framework and one test process per resource are supported.</remarks>
[AspireDto]
[Experimental(MsTestDiagnostics.DiagnosticId, UrlFormat = MsTestDiagnostics.UrlFormat)]
public sealed class MsTestOptions
{
    /// <summary>
    /// Gets the build configuration, or null to use the project's default.
    /// </summary>
    [Experimental(MsTestDiagnostics.DiagnosticId, UrlFormat = MsTestDiagnostics.UrlFormat)]
    public string? Configuration { get; init; }

    /// <summary>
    /// Gets the target framework to run. This must be specified for multi-targeted projects.
    /// </summary>
    [Experimental(MsTestDiagnostics.DiagnosticId, UrlFormat = MsTestDiagnostics.UrlFormat)]
    public string? TargetFramework { get; init; }

    /// <summary>
    /// Gets arguments passed to the embedded runner, for example a filter or settings file.
    /// </summary>
    /// <remarks>
    /// Arguments are individual tokens, not shell-quoted strings. Report destination options,
    /// response files, and server mode are reserved by the integration.
    /// </remarks>
    [Experimental(MsTestDiagnostics.DiagnosticId, UrlFormat = MsTestDiagnostics.UrlFormat)]
    public string[]? Args { get; init; }
}
