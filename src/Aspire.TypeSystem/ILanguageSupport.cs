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

    /// <summary>
    /// Gets the spec for spawning integration hosts in this language, or
    /// <c>null</c> if this language cannot host cross-language integrations.
    /// Default is <c>null</c> — languages opt in by overriding.
    /// </summary>
    IntegrationHostSpec? GetIntegrationHostSpec() => null;
}
