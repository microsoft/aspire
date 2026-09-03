// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Factory for creating AppHost projects from resolved language information.
/// </summary>
internal sealed class AppHostProjectFactory : IAppHostProjectFactory
{
    private readonly DotNetAppHostProject _dotNetProject;
    private readonly CliManagedDotNetAppHostProject _cliManagedDotNetProject;
    private readonly Func<LanguageInfo, GuestAppHostProject> _guestProjectFactory;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly ILogger<AppHostProjectFactory> _logger;

    public AppHostProjectFactory(
        DotNetAppHostProject dotNetProject,
        CliManagedDotNetAppHostProject cliManagedDotNetProject,
        Func<LanguageInfo, GuestAppHostProject> guestProjectFactory,
        ILanguageDiscovery languageDiscovery,
        ILogger<AppHostProjectFactory> logger)
    {
        _dotNetProject = dotNetProject;
        _cliManagedDotNetProject = cliManagedDotNetProject;
        _guestProjectFactory = guestProjectFactory;
        _languageDiscovery = languageDiscovery;
        _logger = logger;
    }

    /// <inheritdoc />
    public IAppHostProject GetProject(LanguageInfo language)
    {
        if (language.LanguageId.Value.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase))
        {
            return _dotNetProject;
        }

        return _guestProjectFactory(language);
    }

    /// <inheritdoc />
    public IAppHostProject? TryGetProject(FileInfo appHostFile)
    {
        _logger.LogDebug("TryGetProject called for file: {AppHostFile}", appHostFile.FullName);

        var language = _languageDiscovery.GetLanguageByFile(appHostFile);
        if (language is null)
        {
            _logger.LogDebug("No language found for file: {AppHostFile}", appHostFile.FullName);
            return null;
        }

        _logger.LogDebug("Language detected: {LanguageId} for file: {AppHostFile}", language.LanguageId.Value, appHostFile.FullName);

        if (language.LanguageId.Value.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase)
            && _cliManagedDotNetProject.CanHandle(appHostFile))
        {
            return _cliManagedDotNetProject;
        }

        return GetProject(language);
    }

    /// <inheritdoc />
    public IAppHostProject GetProject(FileInfo appHostFile)
    {
        var project = TryGetProject(appHostFile);
        if (project is null)
        {
            throw new NotSupportedException($"No handler available for AppHost file '{appHostFile.Name}'.");
        }
        return project;
    }
}
