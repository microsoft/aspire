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

    protected override bool UpdateNotificationsEnabled => true;

    private readonly IProjectLocator _projectLocator;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly IDotNetCliRunner _runner;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IInteractionService _interactionService;
    private readonly ILogger<RestoreCommand> _logger;
    private readonly ICSharpCliManagedAppHostModuleGenerator _cliManagedModuleGenerator;
    private readonly IIntegrationClosureRestorer _integrationClosureRestorer;
    private readonly IFeatures _features;

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    public RestoreCommand(
        IProjectLocator projectLocator,
        IAppHostProjectFactory projectFactory,
        ILanguageDiscovery languageDiscovery,
        IDotNetCliRunner runner,
        IDotNetSdkInstaller sdkInstaller,
        ILogger<RestoreCommand> logger,
        ICSharpCliManagedAppHostModuleGenerator cliManagedModuleGenerator,
        IIntegrationClosureRestorer integrationClosureRestorer,
        CommonCommandServices services)
        : base("restore", RestoreCommandStrings.Description, services)
    {
        _projectLocator = projectLocator;
        _projectFactory = projectFactory;
        _languageDiscovery = languageDiscovery;
        _runner = runner;
        _sdkInstaller = sdkInstaller;
        _interactionService = services.InteractionService;
        _logger = logger;
        _cliManagedModuleGenerator = cliManagedModuleGenerator;
        _integrationClosureRestorer = integrationClosureRestorer;
        _features = services.Features;

        Options.Add(s_appHostOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
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
                    async () => await configOnlyGuestProject.BuildAndGenerateSdkAsync(configOnlyProjectDirectory, cancellationToken: cancellationToken),
                    emoji: KnownEmojis.Gear);

                if (success)
                {
                    _interactionService.DisplaySuccess(
                        string.Format(CultureInfo.CurrentCulture, RestoreCommandStrings.RestoreSucceeded, AspireConfigFile.FileName));
                    return CommandResult.Success();
                }

                return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts);
            }

            if (effectiveAppHostFile is null)
            {
                return CommandResult.Failure(CliExitCodes.FailedToFindProject);
            }

            var project = _projectFactory.TryGetProject(effectiveAppHostFile);

            if (project is null)
            {
                return CommandResult.Failure(CliExitCodes.FailedToFindProject, RestoreCommandStrings.UnrecognizedAppHostType);
            }

            if (project is DotNetAppHostProject)
            {
                if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, InteractionService, Telemetry, cancellationToken: cancellationToken))
                {
                    return CommandResult.Failure(CliExitCodes.SdkNotInstalled);
                }

                var appHostDirectory = effectiveAppHostFile.Directory!;
                _logger.LogDebug("Restoring packages for {AppHost} in {Directory}", effectiveAppHostFile.FullName, appHostDirectory.FullName);

                // For CLI-managed file-based AppHosts, route restore through IntegrationClosureRestorer
                // so the integration closure cache under .aspire/integrations/apphosts/<hash>/ is
                // materialized with a probe manifest the runtime AppHost can consume. This mirrors
                // the PrebuiltAppHostServer restore path used for polyglot AppHosts.
                if (DotNetAppHostProject.IsCliManagedSingleFileAppHost(effectiveAppHostFile, _features))
                {
                    // Wire the module build output through an OutputCollector so the user sees
                    // diagnostics (NU1101 for a missing package, NU1605 for downgrades, etc.) inline
                    // when restore fails, instead of only "See logs at ...". Mirrors how AddCommand
                    // surfaces failures via DisplayLines on the captured output.
                    var buildOutputCollector = new OutputCollector();
                    var buildOptions = new ProcessInvocationOptions
                    {
                        StandardOutputCallback = buildOutputCollector.AppendOutput,
                        StandardErrorCallback = buildOutputCollector.AppendError,
                    };

                    var layout = await _interactionService.ShowStatusAsync(
                        RestoreCommandStrings.RestoringSdkCode,
                        async () => await _integrationClosureRestorer.RestoreAsync(
                            effectiveAppHostFile,
                            new IntegrationClosureRestoreOptions { BuildInvocationOptions = buildOptions },
                            cancellationToken),
                        emoji: KnownEmojis.Gear);

                    if (layout is null)
                    {
                        _interactionService.DisplayLines(buildOutputCollector.GetLines());
                        return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts);
                    }

                    _interactionService.DisplaySuccess(
                        string.Format(CultureInfo.CurrentCulture, RestoreCommandStrings.RestoreSucceeded, effectiveAppHostFile.Name));
                    return CommandResult.Success();
                }

                await _cliManagedModuleGenerator.TryGenerateAsync(effectiveAppHostFile, cancellationToken);

                var restoreExitCode = await _interactionService.ShowStatusAsync(
                    RestoreCommandStrings.RestoringSdkCode,
                    async () => await _runner.RestoreAsync(effectiveAppHostFile, new ProcessInvocationOptions(), cancellationToken),
                    emoji: KnownEmojis.Gear);

                if (restoreExitCode == 0)
                {
                    _interactionService.DisplaySuccess(
                        string.Format(CultureInfo.CurrentCulture, RestoreCommandStrings.RestoreSucceeded, effectiveAppHostFile.Name));
                    return CommandResult.Success();
                }

                return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts);
            }

            if (project is GuestAppHostProject guestProject)
            {
                var directory = effectiveAppHostFile.Directory!;
                _logger.LogDebug("Restoring SDK code for {AppHost} in {Directory}", effectiveAppHostFile.FullName, directory.FullName);

                var success = await _interactionService.ShowStatusAsync(
                    RestoreCommandStrings.RestoringSdkCode,
                    async () => await guestProject.BuildAndGenerateSdkAsync(directory, cancellationToken: cancellationToken),
                    emoji: KnownEmojis.Gear);

                if (success)
                {
                    _interactionService.DisplaySuccess(
                        string.Format(CultureInfo.CurrentCulture, RestoreCommandStrings.RestoreSucceeded, effectiveAppHostFile.Name));
                    return CommandResult.Success();
                }

                return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts);
            }

            return CommandResult.Failure(CliExitCodes.FailedToFindProject, RestoreCommandStrings.UnrecognizedAppHostType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Cancelled();
        }
        catch (ProjectLocatorException ex)
        {
            return HandleProjectLocatorException(ex, InteractionService, Telemetry);
        }
        catch (Exception ex)
        {
            var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts, errorMessage);
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
