// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Test implementation of <see cref="ILanguageDiscovery"/> that includes C# support for testing.
/// Optionally accepts additional languages for polyglot scenarios.
/// </summary>
internal sealed class TestLanguageDiscovery : ILanguageDiscovery
{
    private static readonly LanguageInfo[] s_defaultLanguages =
    [
        new LanguageInfo(
            LanguageId: new LanguageId(KnownLanguageId.CSharp),
            DisplayName: KnownLanguageId.CSharpDisplayName,
            PackageName: "",
            DetectionPatterns: ["*.csproj", "*.fsproj", "*.vbproj", "apphost.cs"],
            CodeGenerator: "",
            AppHostFileName: null),
    ];

    private readonly LanguageInfo[] _allLanguages;

    public TestLanguageDiscovery(params LanguageInfo[] additionalLanguages)
    {
        _allLanguages = [.. s_defaultLanguages, .. additionalLanguages];
    }

    public Task<IEnumerable<LanguageInfo>> GetAvailableLanguagesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<LanguageInfo>>(_allLanguages);

    public Task<string?> GetPackageForLanguageAsync(LanguageId languageId, CancellationToken cancellationToken = default)
    {
        var language = _allLanguages.FirstOrDefault(l =>
            string.Equals(l.LanguageId.Value, languageId.Value, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(language?.PackageName);
    }

    public Task<LanguageId?> DetectLanguageAsync(DirectoryInfo directory, CancellationToken cancellationToken = default)
    {
        foreach (var language in _allLanguages)
        {
            foreach (var pattern in language.DetectionPatterns)
            {
                if (pattern.StartsWith("*.", StringComparison.Ordinal))
                {
                    var extension = pattern[1..];
                    if (directory.EnumerateFiles().Any(f => f.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                    {
                        return Task.FromResult<LanguageId?>(language.LanguageId);
                    }
                }
                else
                {
                    var filePath = Path.Combine(directory.FullName, pattern);
                    if (File.Exists(filePath))
                    {
                        return Task.FromResult<LanguageId?>(language.LanguageId);
                    }
                }
            }
        }
        return Task.FromResult<LanguageId?>(null);
    }

    public LanguageInfo? GetLanguageById(LanguageId languageId)
    {
        return _allLanguages.FirstOrDefault(l =>
            string.Equals(l.LanguageId.Value, languageId.Value, StringComparison.OrdinalIgnoreCase));
    }

    public LanguageInfo? GetLanguageByFile(FileInfo file)
    {
        return _allLanguages.FirstOrDefault(l =>
            l.DetectionPatterns.Any(p => MatchesPattern(file.Name, p)));
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var extension = pattern[1..];
            return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        }
        return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
