// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Aspire.Cli.Configuration;
using Aspire.Cli.Exceptions;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class UpdateCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly IProjectLocator _projectLocator;
    private readonly IPackagingService _packagingService;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly ILogger<UpdateCommand> _logger;
    private readonly ICliDownloader? _cliDownloader;
    private readonly ICliUpdateNotifier _updateNotifier;
    private readonly IFeatures _features;
    private readonly IConfigurationService _configurationService;
    private readonly IConfiguration _configuration;

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", UpdateCommandStrings.ProjectArgumentDescription);
    private static readonly Option<bool> s_selfOption = new("--self")
    {
        Description = UpdateCommandStrings.SelfOptionDescription
    };
    private static readonly Option<bool> s_yesOption = new("--yes")
    {
        Description = UpdateCommandStrings.YesOptionDescription,
        Aliases = { "-y" }
    };
    private static readonly Option<string?> s_nugetConfigDirOption = new("--nuget-config-dir")
    {
        Description = UpdateCommandStrings.NuGetConfigDirOptionDescription
    };
    private readonly Option<string?> _channelOption;
    private readonly Option<string?> _qualityOption;

    public UpdateCommand(
        IProjectLocator projectLocator,
        IPackagingService packagingService,
        IAppHostProjectFactory projectFactory,
        ILogger<UpdateCommand> logger,
        ICliDownloader? cliDownloader,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IConfigurationService configurationService,
        AspireCliTelemetry telemetry,
        IConfiguration configuration)
        : base("update", UpdateCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _projectLocator = projectLocator;
        _packagingService = packagingService;
        _projectFactory = projectFactory;
        _logger = logger;
        _cliDownloader = cliDownloader;
        _updateNotifier = updateNotifier;
        _features = features;
        _configurationService = configurationService;
        _configuration = configuration;

        Options.Add(s_appHostOption);
        Options.Add(s_selfOption);
        Options.Add(s_yesOption);
        Options.Add(s_nugetConfigDirOption);

        // Customize description based on whether staging channel is enabled
        var isStagingEnabled = KnownFeatures.IsStagingChannelEnabled(_features, _configuration);

        _channelOption = new Option<string?>("--channel")
        {
            Description = isStagingEnabled
                ? UpdateCommandStrings.ChannelOptionDescriptionWithStaging
                : UpdateCommandStrings.ChannelOptionDescription
        };
        Options.Add(_channelOption);

        // Keep --quality for backward compatibility but hide it
        _qualityOption = new Option<string?>("--quality")
        {
            Description = isStagingEnabled
                ? UpdateCommandStrings.QualityOptionDescriptionWithStaging
                : UpdateCommandStrings.QualityOptionDescription,
            Hidden = true
        };
        Options.Add(_qualityOption);
    }

    protected override bool UpdateNotificationsEnabled => false;

    private static string? GetDotNetToolUpdateCommand()
    {
        return DotNetToolDetection.GetDotNetToolUpdateCommand();
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var isSelfUpdate = parseResult.GetValue(s_selfOption);

        // If --self is specified, handle CLI self-update
        if (isSelfUpdate)
        {
            // When running as a dotnet tool, print the update command instead of executing
            var dotNetToolUpdateCommand = GetDotNetToolUpdateCommand();
            if (dotNetToolUpdateCommand is not null)
            {
                InteractionService.DisplayMessage(KnownEmojis.Information, UpdateCommandStrings.DotNetToolSelfUpdateMessage);
                InteractionService.DisplayPlainText($"  {dotNetToolUpdateCommand}");
                return 0;
            }

            if (_cliDownloader is null)
            {
                InteractionService.DisplayError("CLI self-update is not available in this environment.");
                return ExitCodeConstants.InvalidCommand;
            }

            try
            {
                return await ExecuteSelfUpdateAsync(parseResult, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                InteractionService.DisplayCancellationMessage();
                return ExitCodeConstants.InvalidCommand;
            }
        }

        // Otherwise, handle project update
        try
        {
            var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
            var projectFile = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, createSettingsFile: true, cancellationToken);
            if (projectFile is null)
            {
                return ExitCodeConstants.FailedToFindProject;
            }

            var project = _projectFactory.GetProject(projectFile);
            var isProjectReferenceMode = project.IsUsingProjectReferences(projectFile);

            // Check if channel or quality option was provided (channel takes precedence)
            var channelName = parseResult.GetValue(_channelOption) ?? parseResult.GetValue(_qualityOption);
            PackageChannel channel;

            var allChannels = await InteractionService.ShowStatusAsync(
                UpdateCommandStrings.CheckingForUpdates,
                async () => await _packagingService.GetChannelsAsync(cancellationToken));

            if (!string.IsNullOrEmpty(channelName))
            {
                // Try to find a channel matching the provided channel/quality
                channel = allChannels.FirstOrDefault(c => string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ChannelNotFoundException($"No channel found matching '{channelName}'. Valid options are: {string.Join(", ", allChannels.Select(c => c.Name))}");
            }
            else if (isProjectReferenceMode)
            {
                channel = allChannels.FirstOrDefault(c => c.Type is PackageChannelType.Implicit)
                    ?? allChannels.First();
            }
            else
            {
                // If there are hives (PR build directories), prompt for channel selection.
                // Otherwise, use the implicit/default channel automatically.
                var hasHives = ExecutionContext.GetPrHiveCount() > 0;

                if (hasHives)
                {
                    // Prompt for channel selection
                    var channelBinding = PromptBinding.Create(parseResult, _channelOption);
                    channel = await InteractionService.PromptForSelectionAsync(
                        UpdateCommandStrings.SelectChannelPrompt,
                        allChannels,
                        (c) => $"{c.Name.EscapeMarkup()} ({c.SourceDetails.EscapeMarkup()})",
                        binding: channelBinding,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    // Use the default (implicit) channel
                    channel = allChannels.FirstOrDefault(c => c.Type is PackageChannelType.Implicit)
                        ?? allChannels.First();
                }
            }

            // Update packages using the appropriate project handler
            var confirmBinding = PromptBinding.CreateWithInteractiveDefault(parseResult, s_yesOption, true);
            var nugetConfigDirBinding = PromptBinding.Create(parseResult, s_nugetConfigDirOption);
            var updateContext = new UpdatePackagesContext
            {
                AppHostFile = projectFile,
                Channel = channel,
                ConfirmBinding = confirmBinding,
                NuGetConfigDirBinding = nugetConfigDirBinding
            };
            await project.UpdatePackagesAsync(updateContext, cancellationToken);

            // After successful project update, check if CLI update is available and prompt
            // Only prompt if the channel supports CLI downloads (has a non-null CliDownloadBaseUrl)
            if (_cliDownloader is not null &&
                _updateNotifier.IsUpdateAvailable() &&
                !string.IsNullOrEmpty(channel.CliDownloadBaseUrl))
            {
                var shouldUpdateCli = await InteractionService.PromptConfirmAsync(
                    UpdateCommandStrings.UpdateCliAfterProjectUpdatePrompt,
                    binding: confirmBinding,
                    cancellationToken: cancellationToken);

                if (shouldUpdateCli)
                {
                    var dotNetToolUpdateCommand = GetDotNetToolUpdateCommand();
                    if (dotNetToolUpdateCommand is not null)
                    {
                        InteractionService.DisplayMessage(KnownEmojis.Information, UpdateCommandStrings.DotNetToolSelfUpdateMessage);
                        InteractionService.DisplayPlainText($"  {dotNetToolUpdateCommand}");
                        return ExitCodeConstants.Success;
                    }

                    // Use the same channel that was selected for the project update
                    return await ExecuteSelfUpdateAsync(parseResult, cancellationToken, channel.Name);
                }
            }
        }
        catch (ProjectUpdaterException ex)
        {
            var message = Markup.Escape(ex.Message);
            Telemetry.RecordError(message, ex);
            InteractionService.DisplayError(message);
            return ExitCodeConstants.FailedToUpgradeProject;
        }
        catch (ChannelNotFoundException ex)
        {
            var message = Markup.Escape(ex.Message);
            Telemetry.RecordError(message, ex);
            InteractionService.DisplayError(message);
            return ExitCodeConstants.FailedToUpgradeProject;
        }
        catch (ProjectLocatorException ex)
        {
            // Check if this is a "no project found" error and prompt for self-update
            if (string.Equals(ex.Message, ErrorStrings.NoProjectFileFound, StringComparisons.CliInputOrOutput))
            {
                // Only prompt for self-update if not running as dotnet tool and downloader is available
                if (GetDotNetToolUpdateCommand() is null && _cliDownloader is not null)
                {
                    var shouldUpdateCli = await InteractionService.PromptConfirmAsync(
                        UpdateCommandStrings.NoAppHostFoundUpdateCliPrompt,
                        binding: PromptBinding.Create(parseResult, s_yesOption, false),
                        cancellationToken: cancellationToken);

                    if (shouldUpdateCli)
                    {
                        return await ExecuteSelfUpdateAsync(parseResult, cancellationToken);
                    }
                }
            }

            return HandleProjectLocatorException(ex, InteractionService, Telemetry);
        }
        catch (OperationCanceledException)
        {
            InteractionService.DisplayCancellationMessage();
            return ExitCodeConstants.FailedToUpgradeProject;
        }

        return 0;
    }

    private async Task<int> ExecuteSelfUpdateAsync(ParseResult parseResult, CancellationToken cancellationToken, string? selectedChannel = null)
    {
        var channel = selectedChannel ?? parseResult.GetValue(_channelOption) ?? parseResult.GetValue(_qualityOption);

        // If channel is not specified, always prompt the user to select one.
        // This ensures they consciously choose a channel that will be saved to global settings
        // for future 'aspire new' and 'aspire init' commands.
        if (string.IsNullOrEmpty(channel))
        {
            var isStagingEnabled = KnownFeatures.IsStagingChannelEnabled(_features, _configuration);
            var channels = isStagingEnabled
                ? new[] { PackageChannelNames.Stable, PackageChannelNames.Staging, PackageChannelNames.Daily }
                : new[] { PackageChannelNames.Stable, PackageChannelNames.Daily };
            var channelBinding = PromptBinding.Create(parseResult, _channelOption);
            channel = await InteractionService.PromptForSelectionAsync(
                "Select the channel to update to:",
                channels,
                q => q,
                binding: channelBinding,
                cancellationToken: cancellationToken);
        }

        try
        {
            // Get current executable path for display purposes only
            var currentExePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExePath))
            {
                InteractionService.DisplayError("Unable to determine the current executable path.");
                return ExitCodeConstants.InvalidCommand;
            }

            InteractionService.DisplayMessage(KnownEmojis.Package, $"Current CLI location: {currentExePath}");
            InteractionService.DisplayMessage(KnownEmojis.UpButton, $"Updating to channel: {channel}");

            // Download the latest CLI
            var archivePath = await _cliDownloader!.DownloadLatestCliAsync(channel, cancellationToken);

            // Extract and update to $HOME/.aspire/bin
            await ExtractAndUpdateAsync(archivePath, cancellationToken);

            // Save the selected channel to global settings for future use with 'aspire new' and 'aspire init'
            // For stable channel, clear the setting to leave it blank (like the install scripts do)
            // For other channels (staging, daily), save the channel name
            if (string.Equals(channel, PackageChannelNames.Stable, StringComparison.OrdinalIgnoreCase))
            {
                await _configurationService.DeleteConfigurationAsync("channel", isGlobal: true, cancellationToken);
                _logger.LogDebug("Cleared global channel setting for stable channel");
            }
            else
            {
                await _configurationService.SetConfigurationAsync("channel", channel, isGlobal: true, cancellationToken);
                _logger.LogDebug("Saved global channel setting: {Channel}", channel);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            InteractionService.DisplayCancellationMessage();
            return ExitCodeConstants.InvalidCommand;
        }
        catch (Exception ex)
        {
            Telemetry.RecordError("Failed to update CLI", ex);
            var errorMessage = $"Failed to update CLI: {ex.Message}";
            InteractionService.DisplayError(errorMessage);
            return ExitCodeConstants.InvalidCommand;
        }
    }

    private async Task ExtractAndUpdateAsync(string archivePath, CancellationToken cancellationToken)
    {
        // Install to the same directory as the current CLI executable
        var currentExePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExePath))
        {
            throw new InvalidOperationException("Unable to determine current CLI location.");
        }

        var installDir = Path.GetDirectoryName(currentExePath);
        if (string.IsNullOrEmpty(installDir))
        {
            throw new InvalidOperationException($"Unable to determine installation directory from: {currentExePath}");
        }

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "aspire.exe" : "aspire";
        var targetExePath = Path.Combine(installDir, exeName);
        var tempExtractDir = Directory.CreateTempSubdirectory("aspire-cli-extract").FullName;

        try
        {
            // Extract archive
            await InteractionService.ShowStatusAsync(
                UpdateCommandStrings.ExtractingNewCli,
                async () =>
                {
                    await ArchiveHelper.ExtractAsync(archivePath, tempExtractDir, cancellationToken);
                    return 0;
                },
                KnownEmojis.Package);

            InteractionService.DisplayMessage(KnownEmojis.Package, UpdateCommandStrings.ExtractedNewCli);

            // Find the aspire executable in the extracted files
            var newExePath = Path.Combine(tempExtractDir, exeName);
            if (!File.Exists(newExePath))
            {
                throw new FileNotFoundException($"Extracted CLI executable not found: {newExePath}");
            }

            // Backup current executable if it exists
            var exeDir = Path.GetDirectoryName(targetExePath)!;
            FileDeleteHelper.TryCleanupOldItems(exeDir, exeName);

            string? backupPath = null;
            if (File.Exists(targetExePath))
            {
                InteractionService.DisplayMessage(KnownEmojis.FloppyDisk, "Backing up current CLI...");

                // Rename current executable to .old.[timestamp]
                var unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                backupPath = $"{targetExePath}.old.{unixTimestamp}";
                _logger.LogDebug("Creating backup: {BackupPath}", backupPath);
                File.Move(targetExePath, backupPath);
            }

            try
            {
                // Copy new executable to install location
                InteractionService.DisplayMessage(KnownEmojis.Wrench, $"Installing new CLI to {installDir}...");
                File.Copy(newExePath, targetExePath, overwrite: true);

                // On Unix systems, ensure the executable bit is set
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    SetExecutablePermission(targetExePath);
                }

                // Test the new executable and display its version
                _logger.LogDebug("Testing new CLI executable and displaying version");
                var newVersion = await GetNewVersionAsync(targetExePath, cancellationToken);
                if (newVersion is null)
                {
                    throw new InvalidOperationException("New CLI executable failed verification test.");
                }

                // If we get here, the update was successful, clean up old backups
                FileDeleteHelper.TryCleanupOldItems(exeDir, exeName);

                // The new binary will extract its embedded bundle on first run via EnsureExtractedAsync.
                // No proactive extraction needed — the payload is inside the new binary's embedded resources,
                // which are only accessible when that binary is running.

                // Display helpful message about PATH
                if (!IsInPath(installDir))
                {
                    InteractionService.DisplayMessage(KnownEmojis.Information, $"Note: {installDir} is not in your PATH. Add it to use the updated CLI globally.");
                }
            }
            catch
            {
                // If anything goes wrong, restore the backup
                _logger.LogWarning("Update failed, restoring backup");
                if (backupPath is not null && File.Exists(backupPath))
                {
                    if (File.Exists(targetExePath))
                    {
                        File.Delete(targetExePath);
                    }
                    File.Move(backupPath, targetExePath);
                }
                throw;
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(
                string.Format(CultureInfo.CurrentCulture, UpdateCommandStrings.NoWritePermissionToInstallDirectory, installDir));
        }
        finally
        {
            // Clean up temp directories
            CleanupDirectory(tempExtractDir);
            CleanupDirectory(Path.GetDirectoryName(archivePath)!);
        }
    }

    private static bool IsInPath(string directory)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return false;
        }

        var pathSeparator = Path.PathSeparator;
        var paths = pathEnv.Split(pathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return paths.Any(p =>
            string.Equals(Path.GetFullPath(p.Trim()), Path.GetFullPath(directory),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal));
    }

    private void SetExecutablePermission(string filePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var mode = File.GetUnixFileMode(filePath);
                mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(filePath, mode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set executable permission on {FilePath}", filePath);
            }
        }
    }

    private async Task<string?> GetNewVersionAsync(string exePath, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                var version = output.Trim();
                InteractionService.DisplaySuccess($"Updated to version: {version}");
                return version;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private void CleanupDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up directory {Directory}", directory);
        }
    }
}
