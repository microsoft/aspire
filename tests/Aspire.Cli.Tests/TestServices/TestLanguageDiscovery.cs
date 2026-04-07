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
        // Flat scan — immediate directory only, using EnumerateFiles for glob support
        foreach (var language in _allLanguages)
        {
            var match = Aspire.Cli.Utils.FileSystemHelper.FindFirstFile(
                directory.FullName,
                recurseLimit: 0,
                language.DetectionPatterns);

            if (match is not null)
            {
                return Task.FromResult<LanguageId?>(language.LanguageId);
            }
        }

        return Task.FromResult<LanguageId?>(null);
    }

    public Task<LanguageId?> DetectLanguageRecursiveAsync(DirectoryInfo directory, CancellationToken cancellationToken = default)
    {
        foreach (var language in _allLanguages)
        {
            if (language.FindInDirectory(directory.FullName) is not null)
            {
                return Task.FromResult<LanguageId?>(language.LanguageId);
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
        return _allLanguages.FirstOrDefault(l => l.MatchesFile(file.Name));
    }
}
