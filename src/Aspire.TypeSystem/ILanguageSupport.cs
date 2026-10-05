// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TypeSystem;

/// <summary>
/// Interface for providing language-specific support for polyglot AppHosts.
/// Implementations provide scaffolding, detection, and runtime execution configuration.
/// </summary>
public interface ILanguageSupport
{
    /// <summary>
    /// Gets the language identifier (e.g., "TypeScript", "Python").
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Gets the environment variable that accepts an additional PEM certificate bundle when running an AppHost for this language.
    /// </summary>
    /// <remarks>
    /// The CLI sets this environment variable only when running the AppHost, not when publishing it.
    /// Return the name of the runtime-specific environment variable, rather than a certificate path.
    /// The runtime uses the certificate bundle as additional trusted roots for the entire AppHost process,
    /// affecting all outbound TLS connections, including connections unrelated to Aspire-managed resources.
    /// Implementations should opt in only when this process-wide trust scope is appropriate.
    /// For example:
    /// <code>
    /// public string? CertificateBundleEnvironmentVariable => "NODE_EXTRA_CA_CERTS";
    /// </code>
    /// </remarks>
    string? CertificateBundleEnvironmentVariable => null;

    /// <summary>
    /// Generates scaffold files for a new project.
    /// </summary>
    /// <param name="request">The scaffold request containing project details.</param>
    /// <returns>A dictionary of relative file paths to file contents.</returns>
    Dictionary<string, string> Scaffold(ScaffoldRequest request);

    /// <summary>
    /// Detects if a directory contains an AppHost for this language.
    /// </summary>
    /// <param name="directoryPath">The directory to check.</param>
    /// <returns>Detection result with language and file information.</returns>
    DetectionResult Detect(string directoryPath);

    /// <summary>
    /// Gets the runtime execution specification for this language.
    /// </summary>
    /// <returns>The runtime spec containing commands for execution.</returns>
    RuntimeSpec GetRuntimeSpec();
}
