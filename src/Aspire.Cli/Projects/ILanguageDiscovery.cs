// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

/// <summary>
/// A strongly-typed identifier for a programming language/runtime.
/// </summary>
/// <param name="Value">The language identifier value (e.g., "typescript/nodejs").</param>
/// <remarks>
/// Using a record struct ensures type safety and prevents accidental mixing of
/// language IDs with other string parameters.
/// </remarks>
internal readonly record struct LanguageId(string Value)
{
    /// <summary>
    /// Implicit conversion to string for convenience.
    /// </summary>
    public static implicit operator string(LanguageId id) => id.Value;

    /// <summary>
    /// Implicit conversion from string for convenience.
    /// </summary>
    public static implicit operator LanguageId(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Information about a supported language.
/// </summary>
/// <param name="LanguageId">The language identifier (e.g., "typescript/nodejs").</param>
/// <param name="DisplayName">The display name for the language (e.g., "TypeScript (Node.js)").</param>
/// <param name="PackageName">The NuGet package name for language support (e.g., "Aspire.Hosting.CodeGeneration.TypeScript").</param>
/// <param name="DetectionPatterns">File patterns used to detect this language (e.g., ["apphost.ts"]).</param>
/// <param name="CodeGenerator">The code generator name to use for this language (e.g., "TypeScript"). Must match ICodeGenerator.Language.</param>
/// <param name="AppHostFileName">The default filename for the AppHost entry point (e.g., "apphost.ts").</param>
/// <param name="IsExperimental">Whether this language is experimental and requires an additional per-language feature flag to be enabled.</param>
internal sealed record LanguageInfo(
    LanguageId LanguageId,
    string DisplayName,
    string PackageName,
    string[] DetectionPatterns,
    string CodeGenerator,
    string? AppHostFileName = null,
    bool IsExperimental = false);

/// <summary>
/// Interface for discovering available languages.
/// Implementations provide language metadata and detection capabilities.
/// </summary>
/// <remarks>
/// This interface is designed to be async to support future implementations
/// that may discover languages from external sources (NuGet, config files, etc.).
/// </remarks>
internal interface ILanguageDiscovery
{
    /// <summary>
    /// Gets all available languages.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All available language information.</returns>
    Task<IEnumerable<LanguageInfo>> GetAvailableLanguagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the NuGet package name for a language.
    /// </summary>
    /// <param name="languageId">The language identifier (e.g., "typescript/nodejs").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The package name, or null if the language is not found.</returns>
    Task<string?> GetPackageForLanguageAsync(LanguageId languageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects the language used in a directory by checking for known file patterns.
    /// This is a fallback detection mechanism when .aspire/settings.json doesn't exist.
    /// </summary>
    /// <param name="directory">The directory to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detected language ID, or null if no language was detected.</returns>
    Task<LanguageId?> DetectLanguageAsync(DirectoryInfo directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets language information by its identifier.
    /// </summary>
    /// <param name="languageId">The language identifier.</param>
    /// <returns>The language info, or null if not found.</returns>
    LanguageInfo? GetLanguageById(LanguageId languageId);

    /// <summary>
    /// Gets language information by detecting from a file.
    /// </summary>
    /// <param name="file">The file to detect language from.</param>
    /// <returns>The language info, or null if not recognized.</returns>
    LanguageInfo? GetLanguageByFile(FileInfo file);
}
