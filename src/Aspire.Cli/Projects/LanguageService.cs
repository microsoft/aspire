// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;

namespace Aspire.Cli.Projects;

/// <summary>
/// Service for managing AppHost language selection and persistence.
/// </summary>
internal sealed class LanguageService : ILanguageService
{
    private const string LanguageConfigKey = "appHost.language";

    private readonly IConfigurationService _configurationService;
    private readonly IInteractionService _interactionService;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly ILanguageDiscovery _languageDiscovery;

    public LanguageService(
        IConfigurationService configurationService,
        IInteractionService interactionService,
        IAppHostProjectFactory projectFactory,
        ILanguageDiscovery languageDiscovery)
    {
        _configurationService = configurationService;
        _interactionService = interactionService;
        _projectFactory = projectFactory;
        _languageDiscovery = languageDiscovery;
    }

    /// <inheritdoc />
    public async Task<IAppHostProject?> GetConfiguredProjectAsync(CancellationToken cancellationToken = default)
    {
        // Try new nested key first, then fall back to legacy flat key.
        // TODO: Remove "language" fallback once legacy .aspire/settings.json is no longer supported.
        // Tracked by https://github.com/microsoft/aspire/issues/15239
        var languageId = await _configurationService.GetConfigurationAsync(LanguageConfigKey, cancellationToken)
            ?? await _configurationService.GetConfigurationAsync("language", cancellationToken);

        if (string.IsNullOrWhiteSpace(languageId))
        {
            return null;
        }

        var language = _languageDiscovery.GetLanguageById(languageId);
        return language is not null ? _projectFactory.GetProject(language) : null;
    }

    /// <inheritdoc />
    public async Task SetLanguageAsync(IAppHostProject project, bool isGlobal = false, CancellationToken cancellationToken = default)
    {
        await SetLanguageAsync(project.LanguageId, isGlobal, cancellationToken);
    }

    private async Task SetLanguageAsync(string languageId, bool isGlobal, CancellationToken cancellationToken)
    {
        await _configurationService.SetConfigurationAsync(
            LanguageConfigKey,
            languageId,
            isGlobal,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IAppHostProject> PromptForProjectAsync(CancellationToken cancellationToken = default)
    {
        var (project, _) = await PromptForProjectWithLanguageAsync(cancellationToken);
        return project;
    }

    /// <summary>
    /// Prompts for project selection and returns both the project and the language info.
    /// </summary>
    private async Task<(IAppHostProject project, LanguageInfo language)> PromptForProjectWithLanguageAsync(CancellationToken cancellationToken)
    {
        // Get all available languages from discovery
        var languages = (await _languageDiscovery.GetAvailableLanguagesAsync(cancellationToken)).ToList();

        // If only one option is available, return it without prompting
        if (languages.Count == 1)
        {
            var lang = languages[0];
            return (_projectFactory.GetProject(lang), lang);
        }

        _interactionService.DisplayEmptyLine();
        _interactionService.DisplayMarkdown("""
            # Select AppHost Language

            Choose the programming language for your Aspire AppHost.
            This selection will be saved for future use.
            """);
        _interactionService.DisplayEmptyLine();

        var selected = await _interactionService.PromptForSelectionAsync(
            "Which language would you like to use?",
            languages,
            lang => lang.DisplayName,
            cancellationToken: cancellationToken);

        return (_projectFactory.GetProject(selected), selected);
    }

    /// <inheritdoc />
    public async Task<IAppHostProject> GetOrPromptForProjectAsync(
        string? explicitLanguageId = null,
        bool saveSelection = true,
        CancellationToken cancellationToken = default)
    {
        // If explicitly specified, use that
        if (!string.IsNullOrWhiteSpace(explicitLanguageId))
        {
            var language = _languageDiscovery.GetLanguageById(explicitLanguageId);
            if (language is null)
            {
                _interactionService.DisplayError($"Unknown language: '{explicitLanguageId}'");
                throw new ArgumentException($"Unknown language: '{explicitLanguageId}'", nameof(explicitLanguageId));
            }
            return _projectFactory.GetProject(language);
        }

        // Check if configured
        var configuredProject = await GetConfiguredProjectAsync(cancellationToken);
        if (configuredProject is not null)
        {
            return configuredProject;
        }

        // Prompt for selection
        var (selectedProject, selectedLanguage) = await PromptForProjectWithLanguageAsync(cancellationToken);

        // Save the language ID
        if (saveSelection)
        {
            await SetLanguageAsync(selectedLanguage.LanguageId, isGlobal: false, cancellationToken);
            _interactionService.DisplayMessage(KnownEmojis.CheckMark, $"Language preference saved to local configuration: {selectedProject.DisplayName}");
        }

        return selectedProject;
    }
}
