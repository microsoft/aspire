// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

/// <summary>
/// JSON-RPC error codes returned by the AppHost server for code-generation failures.
/// </summary>
/// <remarks>
/// Values mirror those defined server-side in <c>Aspire.Hosting.RemoteHost</c>.
/// </remarks>
internal static class AppHostCodeGenerationErrorCodes
{
    /// <summary>
    /// The AppHost server failed to load reflection-based code generation metadata.
    /// Typically caused by an assembly-version mismatch between the bundled
    /// <c>Aspire.Hosting</c> runtime and the user-restored integration assemblies.
    /// </summary>
    public const int IncompatibleAspireSdk = -32050;
}

/// <summary>
/// CLI-side representation of the structured diagnostic payload the AppHost server attaches to
/// code-generation failures (mirrors <c>CodeGenerationDiagnostic</c> in
/// <c>Aspire.Hosting.RemoteHost</c>). The JSON shape is contractual; updating one side without
/// the other will break the round-trip.
/// </summary>
internal sealed class AppHostCodeGenerationDiagnostic
{
    /// <summary>
    /// Gets the CLR type name of the original exception thrown by the AppHost server
    /// (e.g. <c>System.TypeLoadException</c>).
    /// </summary>
    public string OriginalExceptionType { get; init; } = "";

    /// <summary>
    /// Gets the name of the type that failed to load, if known.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// Gets the name of the missing member, if the failure was a missing-method or
    /// missing-field error.
    /// </summary>
    public string? MemberName { get; init; }

    /// <summary>
    /// Gets the informational version of the bundled <c>Aspire.Hosting</c> assembly on the
    /// server side, if it could be discovered.
    /// </summary>
    public string? RuntimeAspireHostingVersion { get; init; }

    /// <summary>
    /// Gets the on-disk location of the bundled <c>Aspire.Hosting</c> assembly, if it could be
    /// discovered.
    /// </summary>
    public string? RuntimeAspireHostingPath { get; init; }

    /// <summary>
    /// Gets the loaded integration assemblies probed by the AppHost server at the time of the
    /// failure.
    /// </summary>
    public List<AppHostLoadedAssemblyInfo> LoadedAssemblies { get; init; } = [];

    /// <summary>
    /// Gets a short, language-agnostic remediation hint suitable for surfacing to AppHost
    /// authors.
    /// </summary>
    public string? RemediationHint { get; init; }
}

/// <summary>
/// Identity information for a single loaded assembly captured at the time of a code-generation
/// failure.
/// </summary>
internal sealed class AppHostLoadedAssemblyInfo
{
    /// <summary>
    /// Gets the simple name of the assembly (e.g. <c>Aspire.Hosting.JavaScript</c>).
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Gets the informational version of the assembly, when present, otherwise the assembly
    /// version.
    /// </summary>
    public string? InformationalVersion { get; init; }

    /// <summary>
    /// Gets the on-disk location of the assembly when available.
    /// </summary>
    public string? Location { get; init; }
}
