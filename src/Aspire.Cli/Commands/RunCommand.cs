// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using StreamJsonRpc;

namespace Aspire.Cli.Commands;

internal sealed class RunCommand : BaseCommand
{
    private readonly IDotNetCliRunner _runner;
    private readonly IInteractionService _interactionService;
    private readonly ICertificateService _certificateService;
    private readonly IProjectLocator _projectLocator;
    private readonly IAnsiConsole _ansiConsole;
    private readonly AspireCliTelemetry _telemetry;
    private readonly IConfiguration _configuration;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IDotNetRuntimeSelector _runtimeSelector;
    private readonly IServiceProvider _serviceProvider;

    public RunCommand(
        IDotNetCliRunner runner,
        IInteractionService interactionService,
        ICertificateService certificateService,
        IProjectLocator projectLocator,
        IAnsiConsole ansiConsole,
        AspireCliTelemetry telemetry,
        IConfiguration configuration,
        IDotNetSdkInstaller sdkInstaller,
        IDotNetRuntimeSelector runtimeSelector,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        IServiceProvider serviceProvider)
        : base("run", RunCommandStrings.Description, features, updateNotifier)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(interactionService);
        ArgumentNullException.ThrowIfNull(certificateService);
        ArgumentNullException.ThrowIfNull(projectLocator);
        ArgumentNullException.ThrowIfNull(ansiConsole);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sdkInstaller);
        ArgumentNullException.ThrowIfNull(runtimeSelector);

        _runner = runner;
        _interactionService = interactionService;
        _certificateService = certificateService;
        _projectLocator = projectLocator;
        _ansiConsole = ansiConsole;
        _telemetry = telemetry;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _sdkInstaller = sdkInstaller;
        _runtimeSelector = runtimeSelector;

        var projectOption = new Option<FileInfo?>("--project");
        projectOption.Description = RunCommandStrings.ProjectArgumentDescription;
        Options.Add(projectOption);

        var watchOption = new Option<bool>("--watch", "-w");
        watchOption.Description = RunCommandStrings.WatchArgumentDescription;
        Options.Add(watchOption);

        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Check if the .NET SDK is available
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, _interactionService, _runtimeSelector, cancellationToken))
        {
            return ExitCodeConstants.SdkNotInstalled;
        }

        var buildOutputCollector = new OutputCollector();
        var runOutputCollector = new OutputCollector();

        (bool IsCompatibleAppHost, bool SupportsBackchannel, string? AspireHostingVersion)? appHostCompatibilityCheck = null;
        try
        {
            using var activity = _telemetry.ActivitySource.StartActivity(this.Name);

            var passedAppHostProjectFile = parseResult.GetValue<FileInfo?>("--project");
            var effectiveAppHostProjectFile = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, cancellationToken);

            if (effectiveAppHostProjectFile is null)
            {
                return ExitCodeConstants.FailedToFindProject;
            }

            var env = new Dictionary<string, string>();

            var debug = parseResult.GetValue<bool>("--debug");

            var waitForDebugger = parseResult.GetValue<bool>("--wait-for-debugger");

            if (waitForDebugger)
            {
                env[KnownConfigNames.WaitForDebugger] = "true";
            }

            await _certificateService.EnsureCertificatesTrustedAsync(_runner, cancellationToken);

            var startDebugSession = ExtensionHelper.IsExtensionHost(_interactionService, out _, out var extensionBackchannel) && string.Equals(await _interactionService.PromptForSelectionAsync(
                RunCommandStrings.PromptForDebugging,
                [TemplatingStrings.Yes, TemplatingStrings.No],
                c => c,
                cancellationToken), TemplatingStrings.Yes, StringComparisons.CliInputOrOutput);

            var watch = parseResult.GetValue<bool>("--watch") || (ExtensionHelper.IsExtensionHost(_interactionService, out _, out _) && !startDebugSession);

            if (!watch)
            {
                var buildOptions = new DotNetCliRunnerInvocationOptions
                {
                    StandardOutputCallback = buildOutputCollector.AppendOutput,
                    StandardErrorCallback = buildOutputCollector.AppendError,
                };

                // The extension host will build the app host project itself, so we don't need to do it here if host exists.
                if (!ExtensionHelper.IsExtensionHost(_interactionService, out _, out extensionBackchannel)
                    || !await extensionBackchannel.HasCapabilityAsync(ExtensionHelper.DevKitCapability, cancellationToken))
                {
                    var buildExitCode = await AppHostHelper.BuildAppHostAsync(_runner, _interactionService, effectiveAppHostProjectFile, buildOptions, cancellationToken);

                    if (buildExitCode != 0)
                    {
                        _interactionService.DisplayLines(buildOutputCollector.GetLines());
                        _interactionService.DisplayError(InteractionServiceStrings.ProjectCouldNotBeBuilt);
                        return ExitCodeConstants.FailedToBuildArtifacts;
                    }
                }
            }

            appHostCompatibilityCheck = await AppHostHelper.CheckAppHostCompatibilityAsync(_runner, _interactionService, effectiveAppHostProjectFile, _telemetry, cancellationToken);

            if (!appHostCompatibilityCheck?.IsCompatibleAppHost ?? throw new InvalidOperationException(RunCommandStrings.IsCompatibleAppHostIsNull))
            {
                return ExitCodeConstants.FailedToDotnetRunAppHost;
            }

            var runOptions = new DotNetCliRunnerInvocationOptions
            {
                StandardOutputCallback = runOutputCollector.AppendOutput,
                StandardErrorCallback = runOutputCollector.AppendError,
                StartDebugSession = startDebugSession
            };

            var backchannelCompletitionSource = new TaskCompletionSource<IAppHostBackchannel>();

            var unmatchedTokens = parseResult.UnmatchedTokens.ToArray();

            var pendingRun = _runner.RunAsync(
                effectiveAppHostProjectFile,
                watch,
                !watch,
                unmatchedTokens,
                env,
                backchannelCompletitionSource,
                runOptions,
                cancellationToken);

            // Wait for the backchannel to be established.
            var backchannel = await _interactionService.ShowStatusAsync(RunCommandStrings.ConnectingToAppHost, async () =>
            {
                return await backchannelCompletitionSource.Task.WaitAsync(cancellationToken);
            });

            var logFile = GetAppHostLogFile();

            var pendingLogCapture = CaptureAppHostLogsAsync(logFile, backchannel, cancellationToken);

            var dashboardUrls = await _interactionService.ShowStatusAsync(RunCommandStrings.StartingDashboard, async () =>
            {
                return await backchannel.GetDashboardUrlsAsync(cancellationToken);
            });

            if (dashboardUrls.DashboardHealthy is false)
            {
                _interactionService.DisplayError(RunCommandStrings.DashboardFailedToStart);
                _interactionService.DisplayLines(runOutputCollector.GetLines());
                return ExitCodeConstants.DashboardFailure;
            }

            _ansiConsole.WriteLine();

            var topGrid = new Grid();
            topGrid.AddColumn();
            topGrid.AddColumn();

            var topPadder = new Padder(topGrid, new Padding(3, 0));

            var dashboardsLocalizedString = RunCommandStrings.Dashboard;
            var logsLocalizedString = RunCommandStrings.Logs;
            var endpointsLocalizedString = RunCommandStrings.Endpoints;

            var longestLocalizedLength = new[] { dashboardsLocalizedString, logsLocalizedString, endpointsLocalizedString }
                .Max(s => s.Length);

            topGrid.Columns[0].Width = longestLocalizedLength + 1;

            topGrid.AddRow(new Align(new Markup($"[bold green]{dashboardsLocalizedString}[/]:"), HorizontalAlignment.Right), new Markup($"[link]{dashboardUrls.BaseUrlWithLoginToken}[/]"));
            if (dashboardUrls.CodespacesUrlWithLoginToken is { } codespacesUrlWithLoginToken)
            {
                topGrid.AddRow(Text.Empty, new Markup($"[link]{codespacesUrlWithLoginToken}[/]"));
            }
            topGrid.AddRow(Text.Empty, Text.Empty);
            topGrid.AddRow(new Align(new Markup($"[bold green]{logsLocalizedString}[/]:"), HorizontalAlignment.Right), new Text(logFile.FullName));

            _ansiConsole.Write(topPadder);

            // Use the presence of CodespacesUrlWithLoginToken to detect codespaces, as this is more reliable
            // than environment variables since it comes from the same backend detection logic
            var isCodespaces = dashboardUrls.CodespacesUrlWithLoginToken is not null;
            var isRemoteContainers = _configuration.GetValue<bool>("REMOTE_CONTAINERS", false);
            var isSshRemote = _configuration.GetValue<string?>("VSCODE_IPC_HOOK_CLI") is not null
                                && _configuration.GetValue<string?>("SSH_CONNECTION") is not null;

            AppendCtrlCMessage(longestLocalizedLength);

            if (isCodespaces || isRemoteContainers || isSshRemote)
            {
                bool firstEndpoint = true;

                try
                {
                    var resourceStates = backchannel.GetResourceStatesAsync(cancellationToken);
                    await foreach (var resourceState in resourceStates.WithCancellation(cancellationToken))
                    {
                        ProcessResourceState(resourceState, (resource, endpoint) =>
                        {
                            // When we are appending endpoints we need
                            // to remove the CTRL-C message that was appended
                            // previously. So we can write the endpoint.
                            // We will append the CTRL-C message again after
                            // writing the endpoint.
                            ClearLines(2);

                            var endpointsGrid = new Grid();
                            endpointsGrid.AddColumn();
                            endpointsGrid.AddColumn();
                            endpointsGrid.Columns[0].Width = longestLocalizedLength + 1;

                            if (firstEndpoint)
                            {
                                endpointsGrid.AddRow(Text.Empty, Text.Empty);
                            }

                            endpointsGrid.AddRow(
                                firstEndpoint ? new Align(new Markup($"[bold green]{endpointsLocalizedString}[/]:"), HorizontalAlignment.Right) : Text.Empty,
                                new Markup($"[bold]{resource}[/] [grey]has endpoint[/] [link={endpoint}]{endpoint}[/]")
                                );

                            var endpointsPadder = new Padder(endpointsGrid, new Padding(3, 0));
                            _ansiConsole.Write(endpointsPadder);
                            firstEndpoint = false;

                            AppendCtrlCMessage(longestLocalizedLength);
                        });
                    }
                }
                catch (ConnectionLostException) when (cancellationToken.IsCancellationRequested)
                {
                    // Just swallow this exception because this is an orderly shutdown of the backchannel.
                }
            }

            await pendingLogCapture;
            return await pendingRun;
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken || ex is ExtensionOperationCanceledException)
        {
            _interactionService.DisplayCancellationMessage();
            return ExitCodeConstants.Success;
        }
        catch (ProjectLocatorException ex) when (string.Equals(ex.Message, ErrorStrings.ProjectFileDoesntExist, StringComparisons.CliInputOrOutput))
        {
            _interactionService.DisplayError(InteractionServiceStrings.ProjectOptionDoesntExist);
            return ExitCodeConstants.FailedToFindProject;
        }
        catch (ProjectLocatorException ex) when (string.Equals(ex.Message, ErrorStrings.MultipleProjectFilesFound, StringComparisons.CliInputOrOutput))
        {
            _interactionService.DisplayError(InteractionServiceStrings.ProjectOptionNotSpecifiedMultipleAppHostsFound);
            return ExitCodeConstants.FailedToFindProject;
        }
        catch (ProjectLocatorException ex) when (string.Equals(ex.Message, ErrorStrings.NoProjectFileFound, StringComparisons.CliInputOrOutput))
        {
            _interactionService.DisplayError(InteractionServiceStrings.ProjectOptionNotSpecifiedNoCsprojFound);
            return ExitCodeConstants.FailedToFindProject;
        }
        catch (AppHostIncompatibleException ex)
        {
            return _interactionService.DisplayIncompatibleVersionError(
                ex,
                appHostCompatibilityCheck?.AspireHostingVersion ?? throw new InvalidOperationException(ErrorStrings.AspireHostingVersionNull)
                );
        }
        catch (CertificateServiceException ex)
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.CertificateTrustError, ex.Message.EscapeMarkup()));
            return ExitCodeConstants.FailedToTrustCertificates;
        }
        catch (FailedToConnectBackchannelConnection ex)
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ErrorConnectingToAppHost, ex.Message.EscapeMarkup()));
            _interactionService.DisplayLines(runOutputCollector.GetLines());
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }
        catch (Exception ex)
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message.EscapeMarkup()));
            _interactionService.DisplayLines(runOutputCollector.GetLines());
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }
    }

    private void ClearLines(int lines)
    {
        if (lines <= 0)
        {
            return;
        }

        for (var i = 0; i < lines; i++)
        {
            _ansiConsole.Write("\u001b[1A");
            _ansiConsole.Write("\u001b[2K"); // Clear the line
        }
    }

    private void AppendCtrlCMessage(int longestLocalizedLength)
    {

        var ctrlCGrid = new Grid();
        ctrlCGrid.AddColumn();
        ctrlCGrid.AddColumn();
        ctrlCGrid.Columns[0].Width = longestLocalizedLength + 1;
        ctrlCGrid.AddRow(Text.Empty, Text.Empty);
        ctrlCGrid.AddRow(new Text(string.Empty), new Markup(RunCommandStrings.PressCtrlCToStopAppHost));

        var ctrlCPadder = new Padder(ctrlCGrid, new Padding(3, 0));
        _ansiConsole.Write(ctrlCPadder);
    }

    private static FileInfo GetAppHostLogFile()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var logsPath = Path.Combine(homeDirectory, ".aspire", "cli", "logs");
        var logFilePath = Path.Combine(logsPath, $"apphost-{Environment.ProcessId}-{DateTime.UtcNow:yyyy-MM-dd-HH-mm-ss}.log");
        var logFile = new FileInfo(logFilePath);
        return logFile;
    }

    private static async Task CaptureAppHostLogsAsync(FileInfo logFile, IAppHostBackchannel backchannel, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();

            if (!logFile.Directory!.Exists)
            {
                logFile.Directory.Create();
            }

            using var streamWriter = new StreamWriter(logFile.FullName, append: true)
            {
                AutoFlush = true
            };

            var logEntries = backchannel.GetAppHostLogEntriesAsync(cancellationToken);

            await foreach (var entry in logEntries.WithCancellation(cancellationToken))
            {
                await streamWriter.WriteLineAsync($"{entry.Timestamp:HH:mm:ss} [{entry.LogLevel}] {entry.CategoryName}: {entry.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            // Swallow the exception if the operation was cancelled.
            return;
        }
        catch (ConnectionLostException) when (cancellationToken.IsCancellationRequested)
        {
            // Just swallow this exception because this is an orderly shutdown of the backchannel.
            return;
        }
    }

    private readonly Dictionary<string, RpcResourceState> _resourceStates = new();

    public void ProcessResourceState(RpcResourceState resourceState, Action<string, string> endpointWriter)
    {
        if (_resourceStates.TryGetValue(resourceState.Resource, out var existingResourceState))
        {
            if (resourceState.Endpoints.Except(existingResourceState.Endpoints) is { } endpoints && endpoints.Any())
            {
                foreach (var endpoint in endpoints)
                {
                    endpointWriter(resourceState.Resource, endpoint);
                }
            }

            _resourceStates[resourceState.Resource] = resourceState;
        }
        else
        {
            if (resourceState.Endpoints is { } endpoints && endpoints.Any())
            {
                foreach (var endpoint in endpoints)
                {
                    endpointWriter(resourceState.Resource, endpoint);
                }
            }

            _resourceStates[resourceState.Resource] = resourceState;
        }
    }
}
