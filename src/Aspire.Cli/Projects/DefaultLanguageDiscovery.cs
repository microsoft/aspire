// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;

namespace Aspire.Cli.Projects;

/// <summary>
/// Default implementation of <see cref="ILanguageDiscovery"/>.
/// Maps language identifiers to their NuGet packages and detection patterns.
/// </summary>
/// <remarks>
/// This implementation provides a static list of supported languages.
/// Experimental languages (Go, Java, Rust) are filtered based on per-language feature flags.
/// </remarks>
internal sealed class DefaultLanguageDiscovery(IFeatures features) : ILanguageDiscovery
{
    private static readonly LanguageInfo[] s_allLanguages =
    [
        new LanguageInfo(
            LanguageId: new LanguageId(KnownLanguageId.CSharp),
            DisplayName: KnownLanguageId.CSharpDisplayName,
            PackageName: "", // C# doesn't need a code generation package
            DetectionPatterns: ["*.csproj", "*.fsproj", "*.vbproj", "apphost.cs"],
            CodeGenerator: "", // C# doesn't use code generation
            AppHostFileName: null), // C# uses .csproj
        new LanguageInfo(
            LanguageId: new LanguageId("typescript/nodejs"),
            DisplayName: "TypeScript (Node.js)",
            PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
            DetectionPatterns: ["apphost.ts"],
            CodeGenerator: "TypeScript", // Matches ICodeGenerator.Language
            AppHostFileName: "apphost.ts"),
        new LanguageInfo(
            LanguageId: new LanguageId(KnownLanguageId.Python),
            DisplayName: KnownLanguageId.PythonDisplayName,
            PackageName: "Aspire.Hosting.CodeGeneration.Python",
            DetectionPatterns: ["apphost.py"],
            CodeGenerator: "Python",
            AppHostFileName: "apphost.py",
            IsExperimental: true),
        new LanguageInfo(
            LanguageId: new LanguageId(KnownLanguageId.Go),
            DisplayName: KnownLanguageId.GoDisplayName,
            PackageName: "Aspire.Hosting.CodeGeneration.Go",
            DetectionPatterns: ["apphost.go"],
            CodeGenerator: "Go",
            AppHostFileName: "apphost.go",
            IsExperimental: true),
        new LanguageInfo(
            LanguageId: new LanguageId(KnownLanguageId.Java),
            DisplayName: KnownLanguageId.JavaDisplayName,
            PackageName: "Aspire.Hosting.CodeGeneration.Java",
            DetectionPatterns: ["AppHost.java"],
            CodeGenerator: "Java",
            AppHostFileName: "AppHost.java",
            IsExperimental: true),
        new LanguageInfo(
            LanguageId: new LanguageId(KnownLanguageId.Rust),
            DisplayName: KnownLanguageId.RustDisplayName,
            PackageName: "Aspire.Hosting.CodeGeneration.Rust",
            DetectionPatterns: ["apphost.rs"],
            CodeGenerator: "Rust",
            AppHostFileName: "apphost.rs",
            IsExperimental: true),
    ];

    private static readonly Dictionary<string, string> s_experimentalFeatureFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        [KnownLanguageId.Python] = KnownFeatures.ExperimentalPolyglotPython,
        [KnownLanguageId.Go] = KnownFeatures.ExperimentalPolyglotGo,
        [KnownLanguageId.Java] = KnownFeatures.ExperimentalPolyglotJava,
        [KnownLanguageId.Rust] = KnownFeatures.ExperimentalPolyglotRust,
    };

    /// <inheritdoc />
    public Task<IEnumerable<LanguageInfo>> GetAvailableLanguagesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(s_allLanguages.Where(IsLanguageEnabled));
    }

    /// <inheritdoc />
    public Task<string?> GetPackageForLanguageAsync(LanguageId languageId, CancellationToken cancellationToken = default)
    {
        var language = s_allLanguages.FirstOrDefault(l =>
            string.Equals(l.LanguageId.Value, languageId.Value, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(language?.PackageName);
    }

    /// <inheritdoc />
    public Task<LanguageId?> DetectLanguageAsync(DirectoryInfo directory, CancellationToken cancellationToken = default)
    {
        foreach (var language in s_allLanguages.Where(IsLanguageEnabled))
        {
            foreach (var pattern in language.DetectionPatterns)
            {
                var filePath = Path.Combine(directory.FullName, pattern);
                if (File.Exists(filePath))
                {
                    return Task.FromResult<LanguageId?>(language.LanguageId);
                }
            }
        }

        return Task.FromResult<LanguageId?>(null);
    }

    /// <inheritdoc />
    public LanguageInfo? GetLanguageById(LanguageId languageId)
    {
        // First try exact match
        var match = s_allLanguages.FirstOrDefault(l =>
            string.Equals(l.LanguageId.Value, languageId.Value, StringComparison.OrdinalIgnoreCase));

        // Try alias match (e.g., "typescript" -> "typescript/nodejs")
        match ??= languageId.Value switch
        {
            KnownLanguageId.TypeScriptAlias => s_allLanguages.FirstOrDefault(l =>
                string.Equals(l.LanguageId.Value, KnownLanguageId.TypeScript, StringComparison.OrdinalIgnoreCase)),
            _ => null
        };

        if (match is not null && !IsLanguageEnabled(match))
        {
            return null;
        }

        return match;
    }

    /// <inheritdoc />
    public LanguageInfo? GetLanguageByFile(FileInfo file)
    {
        var match = s_allLanguages.FirstOrDefault(l =>
            l.DetectionPatterns.Any(p => MatchesPattern(file.Name, p)));

        if (match is not null && !IsLanguageEnabled(match))
        {
            return null;
        }

        return match;
    }

    private bool IsLanguageEnabled(LanguageInfo language)
    {
        if (!language.IsExperimental)
        {
            return true;
        }

        if (s_experimentalFeatureFlags.TryGetValue(language.LanguageId.Value, out var featureFlag))
        {
            return features.IsFeatureEnabled(featureFlag, false);
        }

        return true;
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        // Handle wildcard patterns like "*.csproj"
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var extension = pattern[1..]; // ".csproj"
            return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        }
        
        // Exact match
        return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
