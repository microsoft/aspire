// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Restores dependencies for .NET AppHost projects and generates SDK code for guest (non-.NET) AppHost projects.
/// For guest AppHosts, always regenerates without checking the hash, unlike <c>aspire run</c> which
/// skips code generation when the package hash is unchanged.
/// </summary>
internal sealed class RestoreCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly IProjectLocator _projectLocator;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly IDotNetCliRunner _runner;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IInteractionService _interactionService;
    private readonly ILogger<RestoreCommand> _logger;

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    public RestoreCommand(
        IProjectLocator projectLocator,
        IAppHostProjectFactory projectFactory,
        ILanguageDiscovery languageDiscovery,
        IDotNetCliRunner runner,
        IDotNetSdkInstaller sdkInstaller,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        ILogger<RestoreCommand> logger,
        AspireCliTelemetry telemetry)
        : base("restore", RestoreCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _projectLocator = projectLocator;
        _projectFactory = projectFactory;
        _languageDiscovery = languageDiscovery;
        _runner = runner;
        _sdkInstaller = sdkInstaller;
        _interactionService = interactionService;
        _logger = logger;

        Options.Add(s_appHostOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);

        try
        {
            using var activity = Telemetry.StartDiagnosticActivity(Name);

            FileInfo? effectiveAppHostFile = null;
            GuestAppHostProject? configOnlyGuestProject = null;
            DirectoryInfo? configOnlyProjectDirectory = null;

            try
            {
                var searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(
                    passedAppHostProjectFile,
                    MultipleAppHostProjectsFoundBehavior.Prompt,
                    createSettingsFile: false,
                    cancellationToken);

                effectiveAppHostFile = searchResult.SelectedProjectFile;
            }
            catch (ProjectLocatorException ex) when (ex.FailureReason is ProjectLocatorFailureReason.NoProjectFileFound or ProjectLocatorFailureReason.ProjectFileDoesntExist)
            {
                (configOnlyGuestProject, configOnlyProjectDirectory) = TryResolveConfigOnlyGuestProject(passedAppHostProjectFile);

                if (configOnlyGuestProject is null || configOnlyProjectDirectory is null)
                {
                    throw;
                }
            }

            if (configOnlyGuestProject is not null && configOnlyProjectDirectory is not null)
            {
                _logger.LogDebug(
                    "Restoring SDK code for config-only guest AppHost in {Directory}",
                    configOnlyProjectDirectory.FullName);

                var success = await _interactionService.ShowStatusAsync(
                    RestoreCommandStrings.RestoringSdkCode,
                    async () => await configOnlyGuestProject.BuildAndGenerateSdkAsync(configOnlyProjectDirectory, cancellationToken),
                    emoji: KnownEmojis.Gear);

                if (success)
                {
                    _interactionService.DisplaySuccess(
                        string.Format(CultureInfo.CurrentCulture, RestoreCommandStrings.RestoreSucceeded, AspireConfigFile.FileName));
                    return ExitCodeConstants.Success;
                }

                return ExitCodeConstants.FailedToBuildArtifacts;
            }

            if (effectiveAppHostFile is null)
            {
                return ExitCodeConstants.FailedToFindProject;
            }

            var project = _projectFactory.TryGetProject(effectiveAppHostFile);

            if (project is null)
            {
                InteractionService.DisplayError(RestoreCommandStrings.UnrecognizedAppHostType);
                return ExitCodeConstants.FailedToFindProject;
            }

            if (project is DotNetAppHostProject)
            {
                if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, InteractionService, Telemetry, cancellationToken))
                {
                    return ExitCodeConstants.SdkNotInstalled;
                }

                var appHostDirectory = effectiveAppHostFile.Directory!;
                _logger.LogDebug("Restoring packages for {AppHost} in {Directory}", effectiveAppHostFile.FullName, appHostDirectory.FullName);

                var restoreExitCode = await _interactionService.ShowStatusAsync(
                    RestoreCommandStrings.RestoringSdkCode,
                    async () => await _runner.RestoreAsync(effectiveAppHostFile, new DotNetCliRunnerInvocationOptions(), cancellationToken),
                    emoji: KnownEmojis.Gear);

                if (restoreExitCode == 0)
                {
                    _interactionService.DisplaySuccess(
                        string.Format(CultureInfo.CurrentCulture, RestoreCommandStrings.RestoreSucceeded, effectiveAppHostFile.Name));
                    return ExitCodeConstants.Success;
                }

                return ExitCodeConstants.FailedToBuildArtifacts;
            }

            if (project is GuestAppHostProject guestProject)
            {
                var directory = effectiveAppHostFile.Directory!;
                _logger.LogDebug("Restoring SDK code for {AppHost} in {Directory}", effectiveAppHostFile.FullName, directory.FullName);

                var success = await _interactionService.ShowStatusAsync(
                    RestoreCommandStrings.RestoringSdkCode,
                    async () => await guestProject.BuildAndGenerateSdkAsync(directory, cancellationToken),
                    emoji: KnownEmojis.Gear);

                if (success)
                {
                    _interactionService.DisplaySuccess(
                        string.Format(CultureInfo.CurrentCulture, RestoreCommandStrings.RestoreSucceeded, effectiveAppHostFile.Name));
                    return ExitCodeConstants.Success;
                }

                return ExitCodeConstants.FailedToBuildArtifacts;
            }

            InteractionService.DisplayError(RestoreCommandStrings.UnrecognizedAppHostType);
            return ExitCodeConstants.FailedToFindProject;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            InteractionService.DisplayCancellationMessage();
            return ExitCodeConstants.Success;
        }
        catch (ProjectLocatorException ex)
        {
            return HandleProjectLocatorException(ex, InteractionService, Telemetry);
        }
        catch (Exception ex)
        {
            var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            InteractionService.DisplayError(errorMessage);
            return ExitCodeConstants.FailedToBuildArtifacts;
        }
    }

    private (GuestAppHostProject? Project, DirectoryInfo? Directory) TryResolveConfigOnlyGuestProject(FileInfo? passedAppHostProjectFile)
    {
        var searchDirectory = GetFallbackSearchDirectory(passedAppHostProjectFile);
        if (searchDirectory is null)
        {
            return (null, null);
        }

        while (searchDirectory is not null)
        {
            AspireConfigFile? config;
            try
            {
                config = AspireConfigFile.Load(searchDirectory.FullName);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Ignoring invalid config while resolving config-only guest AppHost in {Directory}", searchDirectory.FullName);
                return (null, null);
            }

            if (config is not null)
            {
                if (!string.IsNullOrWhiteSpace(config.AppHost?.Path))
                {
                    return (null, null);
                }

                if (!string.IsNullOrWhiteSpace(config.AppHost?.Language))
                {
                    var language = _languageDiscovery.GetLanguageById(config.AppHost.Language);
                    if (language is null)
                    {
                        _logger.LogDebug("Configured AppHost language '{Language}' is not available for config-only restore in {Directory}", config.AppHost.Language, searchDirectory.FullName);
                        return (null, null);
                    }

                    if (_projectFactory.GetProject(language) is GuestAppHostProject guestProject)
                    {
                        _logger.LogInformation(
                            "Using config-only guest AppHost restore for language {Language} in {Directory}",
                            language.LanguageId.Value,
                            searchDirectory.FullName);
                        return (guestProject, searchDirectory);
                    }

                    return (null, null);
                }
            }

            searchDirectory = searchDirectory.Parent;
        }

        return (null, null);
    }

    private DirectoryInfo? GetFallbackSearchDirectory(FileInfo? passedAppHostProjectFile)
    {
        if (passedAppHostProjectFile is null)
        {
            return ExecutionContext.WorkingDirectory;
        }

        if (Directory.Exists(passedAppHostProjectFile.FullName))
        {
            return new DirectoryInfo(passedAppHostProjectFile.FullName);
        }

        return null;
    }
}
