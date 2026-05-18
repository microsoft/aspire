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

        AddNonInteractiveRequiresYesValidator(this, s_yesOption);

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

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
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
                return CommandResult.FromExitCode(0);
            }

            if (_cliDownloader is null)
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, "CLI self-update is not available in this environment.");
            }

            try
            {
                return await ExecuteSelfUpdateAsync(parseResult, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return CommandResult.Cancelled();
            }
        }

        // Otherwise, handle project update
        try
        {
            var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);

            // `aspire update` is a recovery tool: when the AppHost's pinned Aspire.AppHost.Sdk
            // can no longer be resolved (e.g. updating from one PR build to another after the
            // hive was refreshed) the configured AppHost path must still be locatable, because
            // rewriting that pin is precisely what this command does. Prefer the settings
            // lookup, which does not MSBuild-validate the path, and only fall through to the
            // strict discovery path when no AppHost is recorded in settings.
            FileInfo? projectFile;
            if (passedAppHostProjectFile is not null)
            {
                projectFile = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, createSettingsFile: true, cancellationToken);
            }
            else
            {
                projectFile = await _projectLocator.GetAppHostFromSettingsAsync(cancellationToken)
                    ?? await _projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true, cancellationToken);
            }
            if (projectFile is null)
            {
                return CommandResult.Failure(CliExitCodes.FailedToFindProject);
            }

            var project = _projectFactory.GetProject(projectFile);
            var isProjectReferenceMode = project.IsUsingProjectReferences(projectFile);

            // Resolve the channel using the documented precedence:
            //   1. explicit --channel / hidden --quality
            //   2. local app config "channel" (relative to the resolved AppHost project, NOT cwd)
            //   3. global config "channel"
            //   4. interactive channel prompt when appropriate (PR hives present)
            //   5. implicit/default channel as the documented fallback
            // The directory-scoped lookup is critical: `aspire update --apphost <elsewhere>`
            // must consult the project's directory tree, not the user's launch cwd. The
            // process-wide IConfiguration is rooted at the launch cwd at startup, so using
            // it here would silently read the wrong app's local config (issue #16650).
            //
            // Step 3 (global config "channel") is intentionally a read-only path: no CLI
            // code path seeds the global "channel" config (neither the acquisition scripts
            // nor `aspire update --self` write it), and the running CLI's channel is
            // already discoverable via the AspireCliChannel assembly metadata. The global
            // read remains so users who explicitly ran `aspire config set -g channel <x>`
            // continue to have their preference honored.
            // TODO: revisit removing the step-3 fallback once telemetry confirms global
            // channel usage is negligible.
            var channelName = parseResult.GetValue(_channelOption) ?? parseResult.GetValue(_qualityOption);
            var channelFromConfig = false;
            if (string.IsNullOrWhiteSpace(channelName))
            {
                var configLookupDirectory = projectFile.Directory ?? ExecutionContext.WorkingDirectory;
                channelName = await _configurationService.GetConfigurationFromDirectoryAsync("channel", configLookupDirectory, cancellationToken);
                channelFromConfig = !string.IsNullOrWhiteSpace(channelName);
            }

            PackageChannel channel;

            var allChannels = await InteractionService.ShowStatusAsync(
                UpdateCommandStrings.CheckingForUpdates,
                async () => await _packagingService.GetChannelsAsync(cancellationToken));

            if (!string.IsNullOrWhiteSpace(channelName))
            {
                // Try to find a channel matching the provided channel/quality
                channel = allChannels.FirstOrDefault(c => string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ChannelNotFoundException($"No channel found matching '{channelName}'. Valid options are: {string.Join(", ", allChannels.Select(c => c.Name))}");

                if (channelFromConfig)
                {
                    _logger.LogDebug("Using channel '{ChannelName}' from configuration.", channel.Name);
                }
            }
            else if (isProjectReferenceMode)
            {
                channel = allChannels.FirstOrDefault(c => c.Type is PackageChannelType.Implicit)
                    ?? allChannels.First();
            }
            else
            {
                // Before falling through to the hives prompt, default to the running CLI's
                // identity channel (the value baked into the assembly via the
                // AspireCliChannel metadata) when it matches a registered channel. Without
                // this, a `pr-<N>` or `daily` CLI updating an AppHost that has no
                // per-project `channel` and no global `channel` config would silently land
                // on the Implicit ("default") channel, which resolves Aspire packages from
                // public NuGet and effectively moves the project to daily even though the
                // running CLI knows which channel it shipped from.
                //
                // `local` is intentionally skipped: a developer-built CLI must not silently
                // pin a real project to a hive that only exists on that machine. We also
                // require the identity to match an entry in `allChannels`, so a stale
                // `pr-<N>` identity (e.g. the matching hive was deleted) falls through to
                // the existing prompt/implicit logic instead of failing.
                var identityChannel = ExecutionContext.IdentityChannel;
                PackageChannel? identityMatch = null;
                if (!string.IsNullOrWhiteSpace(identityChannel)
                    && !string.Equals(identityChannel, PackageChannelNames.Local, StringComparison.OrdinalIgnoreCase))
                {
                    identityMatch = allChannels.FirstOrDefault(c => string.Equals(c.Name, identityChannel, StringComparison.OrdinalIgnoreCase));
                }

                if (identityMatch is not null)
                {
                    _logger.LogDebug("Defaulting to identity channel '{ChannelName}'.", identityMatch.Name);
                    channel = identityMatch;
                }
                else
                {
                    // If there are hives (PR build directories), prompt for channel selection.
                    // Otherwise, use the implicit/default channel automatically.
                    var hasHives = ExecutionContext.GetHiveCount() > 0;

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
            }

            // Update packages using the appropriate project handler
            // The validator ensures --yes is required when --non-interactive is specified,
            // so by this point --yes is always explicitly provided in non-interactive mode.
            // defaultValue: true means the interactive prompt defaults to "yes" (accept).
            var confirmBinding = PromptBinding.Create(parseResult, s_yesOption, defaultValue: true);
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
                        return CommandResult.Success();
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
            return CommandResult.Failure(CliExitCodes.FailedToUpgradeProject, message);
        }
        catch (ChannelNotFoundException ex)
        {
            var message = Markup.Escape(ex.Message);
            Telemetry.RecordError(message, ex);
            return CommandResult.Failure(CliExitCodes.FailedToUpgradeProject, message);
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
            return CommandResult.Cancelled();
        }

        return CommandResult.FromExitCode(0);
    }

    private async Task<CommandResult> ExecuteSelfUpdateAsync(ParseResult parseResult, CancellationToken cancellationToken, string? selectedChannel = null)
    {
        var channel = selectedChannel ?? parseResult.GetValue(_channelOption) ?? parseResult.GetValue(_qualityOption);

        // If channel is not specified, prompt the user to select one. The choice
        // applies only to this self-update invocation; subsequent 'aspire new'
        // and 'aspire init' commands resolve channel per-project from
        // aspire.config.json, not from any global setting.
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
                return CommandResult.Failure(CliExitCodes.InvalidCommand, "Unable to determine the current executable path.");
            }

            InteractionService.DisplayMessage(KnownEmojis.Package, $"Current CLI location: {currentExePath}");
            InteractionService.DisplayMessage(KnownEmojis.UpButton, $"Updating to channel: {channel}");

            // Download the latest CLI
            var archivePath = await _cliDownloader!.DownloadLatestCliAsync(channel, cancellationToken);

            // Extract and update to $HOME/.aspire/bin
            await ExtractAndUpdateAsync(archivePath, cancellationToken);

            return CommandResult.Success();
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Cancelled();
        }
        catch (Exception ex)
        {
            Telemetry.RecordError("Failed to update CLI", ex);
            var errorMessage = $"Failed to update CLI: {ex.Message}";
            return CommandResult.Failure(CliExitCodes.InvalidCommand, errorMessage);
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
