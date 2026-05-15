// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Aspire.Cli.Acquisition;
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
    private readonly IInstallationDiscovery _installationDiscovery;
    private readonly IUpgradeInstructionProvider _upgradeInstructionProvider;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly WingetFirstRunProbe _wingetFirstRunProbe;

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", UpdateCommandStrings.ProjectArgumentDescription);
    private static readonly Option<bool> s_selfOption = new("--self")
    {
        Description = UpdateCommandStrings.SelfOptionDescription
    };
    private static readonly Option<bool> s_forceOption = new("--force")
    {
        Description = UpdateCommandStrings.ForceOptionDescription
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
        IConfiguration configuration,
        IInstallationDiscovery installationDiscovery,
        IUpgradeInstructionProvider upgradeInstructionProvider,
        ICliHostEnvironment hostEnvironment,
        WingetFirstRunProbe wingetFirstRunProbe)
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
        _installationDiscovery = installationDiscovery;
        _upgradeInstructionProvider = upgradeInstructionProvider;
        _hostEnvironment = hostEnvironment;
        _wingetFirstRunProbe = wingetFirstRunProbe;

        Options.Add(s_appHostOption);
        Options.Add(s_selfOption);
        Options.Add(s_forceOption);
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

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var isSelfUpdate = parseResult.GetValue(s_selfOption);

        // If --self is specified, handle CLI self-update
        if (isSelfUpdate)
        {
            return await HandleSelfUpdateAsync(parseResult, cancellationToken);
        }

        // Otherwise, handle project update
        try
        {
            var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
            var projectFile = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, createSettingsFile: true, cancellationToken);
            if (projectFile is null)
            {
                return CommandResult.Failure(ExitCodeConstants.FailedToFindProject);
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
            // Step 3 (global config "channel") is a transitional read-only path: the CLI no
            // longer WRITES the global channel (acquisition scripts and `aspire update --self`
            // both stopped seeding it), and identity-channel is baked into the binary via
            // AspireCliChannel metadata. The global read remains so users who deliberately ran
            // `aspire config set -g channel <x>` on the new CLI keep their preference honored.
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
                // If there are hives (PR build directories) AND interactive
                // input is available, prompt the user to pick a channel. In
                // non-interactive mode the prompt would crash (#15600), so
                // fall through to the implicit/default channel — same
                // behavior as the no-hives branch.
                var hasHives = ExecutionContext.GetHiveCount() > 0;

                if (hasHives && _hostEnvironment.SupportsInteractiveInput)
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
                    var (source, canonicalPath) = ResolveRunningInstall();
                    if (SelfUpdateRouter.GetAction(source) == SelfUpdateAction.Delegate)
                    {
                        return DisplaySelfUpdateRefusal(source, canonicalPath);
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
            return CommandResult.Failure(ExitCodeConstants.FailedToUpgradeProject, message);
        }
        catch (ChannelNotFoundException ex)
        {
            var message = Markup.Escape(ex.Message);
            Telemetry.RecordError(message, ex);
            return CommandResult.Failure(ExitCodeConstants.FailedToUpgradeProject, message);
        }
        catch (ProjectLocatorException ex)
        {
            // Check if this is a "no project found" error and prompt for self-update
            if (string.Equals(ex.Message, ErrorStrings.NoProjectFileFound, StringComparisons.CliInputOrOutput))
            {
                // Only prompt for self-update if not running as dotnet tool and downloader is available
                if (!DotNetToolDetection.IsRunningAsDotNetTool() && _cliDownloader is not null)
                {
                    var shouldUpdateCli = await InteractionService.PromptConfirmAsync(
                        UpdateCommandStrings.NoAppHostFoundUpdateCliPrompt,
                        binding: PromptBinding.Create(parseResult, s_yesOption, false),
                        cancellationToken: cancellationToken);

                    if (shouldUpdateCli)
                    {
                        var (source, canonicalPath) = ResolveRunningInstall();
                        if (SelfUpdateRouter.GetAction(source) == SelfUpdateAction.Delegate)
                        {
                            return DisplaySelfUpdateRefusal(source, canonicalPath);
                        }

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

    /// <summary>
    /// Routes <c>aspire update --self</c> based on the running CLI's install source.
    /// Script installs perform the existing in-process binary swap; every other
    /// route is refused with an installer-appropriate command from
    /// <see cref="IUpgradeInstructionProvider"/> so we don't corrupt the
    /// package-manager-owned binary or silently demote a PR-pinned install to
    /// stable. Unknown / missing sidecars are refused defensively.
    /// </summary>
    private async Task<CommandResult> HandleSelfUpdateAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var (source, canonicalPath) = ResolveRunningInstall();
        var force = parseResult.GetValue(s_forceOption);
        var action = force ? SelfUpdateAction.InProcess : SelfUpdateRouter.GetAction(source);

        if (force)
        {
            _logger.LogDebug("Forcing in-process self-update for detected install source '{InstallSource}' at '{CanonicalPath}'.", source, canonicalPath);
        }

        if (action == SelfUpdateAction.Delegate)
        {
            return DisplaySelfUpdateRefusal(source, canonicalPath);
        }

        // Script route, or explicit --force: existing in-process flow.
        if (_cliDownloader is null)
        {
            return CommandResult.Failure(ExitCodeConstants.InvalidCommand, "CLI self-update is not available in this environment.");
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

    /// <summary>
    /// Resolves the install source and canonical binary path for the running CLI
    /// via <see cref="IInstallationDiscovery.DescribeSelf"/> (the single source of
    /// truth for "what is the running binary?"). Falls back to
    /// <see cref="DotNetToolDetection"/> path-shape detection when no sidecar is
    /// present, so legacy dotnet-tool installs created before the sidecar
    /// contract shipped still get classified as <see cref="InstallSource.DotnetTool"/>
    /// rather than <see cref="InstallSource.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// When the initial discovery reports no route (i.e., no sidecar on disk
    /// yet), runs <see cref="WingetFirstRunProbe"/> on the binary directory
    /// derived from the discovery's canonical path, then re-describes so the
    /// freshly-stamped sidecar is picked up. Without this, a fresh WinGet
    /// install whose very first invocation is <c>aspire update --self</c>
    /// would classify as <see cref="InstallSource.Unknown"/> and be refused
    /// until the user investigates or explicitly passes <c>--force</c>.
    /// The probe is idempotent and self-gates via
    /// <see cref="IWindowsRegistryReader"/>, so the call is a cheap no-op
    /// on non-Windows / non-WinGet installs.
    /// </remarks>
    private (InstallSource Source, string? CanonicalPath) ResolveRunningInstall()
    {
        var info = _installationDiscovery.DescribeSelf();
        var canonicalPath = info.CanonicalPath ?? info.Path;

        if (string.IsNullOrEmpty(info.Route) && !string.IsNullOrEmpty(canonicalPath))
        {
            var binaryDir = Path.GetDirectoryName(canonicalPath);
            if (!string.IsNullOrEmpty(binaryDir))
            {
                _wingetFirstRunProbe.Run(binaryDir);
                info = _installationDiscovery.DescribeSelf();
                canonicalPath = info.CanonicalPath ?? info.Path;
            }
        }

        var source = InstallSourceExtensions.ParseInstallSource(info.Route);

        // No-arg DotNetToolDetection.IsRunningAsDotNetTool honors the
        // s_processPathOverride AsyncLocal used by tests, so this fallback
        // recognizes legacy dotnet-tool installs (no sidecar baked) AND
        // remains testable via UseProcessPathForTesting.
        if (source == InstallSource.Unknown && DotNetToolDetection.IsRunningAsDotNetTool())
        {
            source = InstallSource.DotnetTool;
        }

        return (source, canonicalPath);
    }

    /// <summary>
    /// Prints the installer-appropriate update command. Returns exit code 0
    /// matching the existing dotnet-tool refusal contract — the CLI performed
    /// its responsibility by telling the user what to run. Users who rely on
    /// exit-code-based detection of "did this update?" should switch to checking
    /// the version after the run, since the command may also succeed without
    /// changing the binary if the user is already on the latest version.
    /// </summary>
    private CommandResult DisplaySelfUpdateRefusal(InstallSource source, string? canonicalPath)
    {
        var command = _upgradeInstructionProvider.GetUpdateCommand(
            source,
            canonicalPath,
            ExecutionContext.IdentityChannel);

        if (command is not null)
        {
            InteractionService.DisplayMessage(KnownEmojis.Information, GetSelfUpdateRefusalMessage(source));
            InteractionService.DisplayPlainText($"  {command}");
            return CommandResult.FromExitCode(0);
        }

        InteractionService.DisplayMessage(KnownEmojis.Warning, UpdateCommandStrings.SelfUpdateUnknownSourceMessage);
        return CommandResult.Failure(ExitCodeConstants.InvalidCommand, "Cannot determine install source for self-update.");
    }

    private static string GetSelfUpdateRefusalMessage(InstallSource source) => source switch
    {
        InstallSource.DotnetTool => UpdateCommandStrings.DotNetToolSelfUpdateMessage,
        InstallSource.Winget => UpdateCommandStrings.WingetSelfUpdateMessage,
        InstallSource.Brew => UpdateCommandStrings.BrewSelfUpdateMessage,
        InstallSource.Pr => UpdateCommandStrings.PrSelfUpdateMessage,
        InstallSource.LocalHive => UpdateCommandStrings.LocalHiveSelfUpdateMessage,
        _ => UpdateCommandStrings.SelfUpdateUnknownSourceMessage,
    };

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
                return CommandResult.Failure(ExitCodeConstants.InvalidCommand, "Unable to determine the current executable path.");
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
            return CommandResult.Failure(ExitCodeConstants.InvalidCommand, errorMessage);
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
