// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
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
    private readonly IDotNetCliRunner _runner;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IInteractionService _interactionService;
    private readonly ILogger<RestoreCommand> _logger;

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    public RestoreCommand(
        IProjectLocator projectLocator,
        IAppHostProjectFactory projectFactory,
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

            var searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(
                passedAppHostProjectFile,
                MultipleAppHostProjectsFoundBehavior.Prompt,
                createSettingsFile: false,
                cancellationToken);

            var effectiveAppHostFile = searchResult.SelectedProjectFile;

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
}
