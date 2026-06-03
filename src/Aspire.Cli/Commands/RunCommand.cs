// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Profiling;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using StreamJsonRpc;

namespace Aspire.Cli.Commands;

/// <summary>
/// Represents information about a detached AppHost for JSON serialization.
/// </summary>
// `aspire start --format json` and `aspire run --detach --format json` use this shape;
// keep docs/specs/cli-output-formats.md in sync when changing it.
internal sealed record DetachOutputInfo(
    string AppHostPath,
    int AppHostPid,
    int CliPid,
    string? DashboardUrl,
    string LogFile);

[JsonSerializable(typeof(DetachOutputInfo))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class RunCommandJsonContext : JsonSerializerContext
{
    private static RunCommandJsonContext? s_relaxedEscaping;

    /// <summary>
    /// Gets a context with relaxed JSON escaping for non-ASCII character support.
    /// </summary>
    public static RunCommandJsonContext RelaxedEscaping => s_relaxedEscaping ??= new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

internal sealed class RunCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly IDotNetCliRunner _runner;
    private readonly ICertificateService _certificateService;
    private readonly IProjectLocator _projectLocator;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    private readonly IFeatures _features;
    private readonly ILogger<RunCommand> _logger;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly AppHostLauncher _appHostLauncher;
    private readonly FileLoggerProvider _fileLoggerProvider;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly ProfilingTelemetry _profilingTelemetry;
    private readonly TimeProvider _timeProvider;
    private bool _isDetachMode;
    private const int MaxDisplayedAppHostStartupOutputLines = 80;

    private static readonly TimeSpan s_appHostStartupCancellationTimeout = TimeSpan.FromSeconds(5);

    // Guest AppHosts can bring up the temporary server/backchannel and then fail immediately
    // afterward when the guest startup process hits a syntax, pre-execute, or model validation
    // error. Keep guest AppHost startup waits alive briefly so those failures are reported instead of hidden.
    private static readonly TimeSpan s_startupFailureObservationWindow = TimeSpan.FromSeconds(2);

    protected override bool UpdateNotificationsEnabled => !_isDetachMode;

    private static readonly Option<bool> s_detachOption = new("--detach")
    {
        Description = RunCommandStrings.DetachArgumentDescription
    };
    private static readonly Option<bool> s_noBuildOption = new("--no-build")
    {
        Description = RunCommandStrings.NoBuildArgumentDescription
    };

    public RunCommand(
        IDotNetCliRunner runner,
        ICertificateService certificateService,
        IProjectLocator projectLocator,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        ILogger<RunCommand> logger,
        IAppHostProjectFactory projectFactory,
        AppHostLauncher appHostLauncher,
        FileLoggerProvider fileLoggerProvider,
        ICliHostEnvironment hostEnvironment,
        ProfilingTelemetry profilingTelemetry,
        TimeProvider timeProvider,
        CommonCommandServices services)
        : base("run", RunCommandStrings.Description, services)
    {
        _runner = runner;
        _certificateService = certificateService;
        _projectLocator = projectLocator;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _features = services.Features;
        _logger = logger;
        _projectFactory = projectFactory;
        _appHostLauncher = appHostLauncher;
        _fileLoggerProvider = fileLoggerProvider;
        _hostEnvironment = hostEnvironment;
        _profilingTelemetry = profilingTelemetry;
        _timeProvider = timeProvider;

        Options.Add(s_detachOption);
        Options.Add(s_noBuildOption);
        AppHostLauncher.AddLaunchOptions(this);

        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var passedAppHostProjectFile = parseResult.GetValue(AppHostLauncher.s_appHostOption);
        var detach = parseResult.GetValue(s_detachOption);
        _isDetachMode = detach;
        var noBuild = parseResult.GetValue(s_noBuildOption);
        var format = parseResult.GetValue(AppHostLauncher.s_formatOption);
        var isolated = parseResult.GetValue(AppHostLauncher.s_isolatedOption);
        var isExtensionHost = ExtensionHelper.IsExtensionHost(InteractionService, out _, out _);
        var captureProfile = parseResult.GetValue(RootCommand.CaptureProfileOption);
        var captureProfileDelay = TimeSpan.FromSeconds(parseResult.GetValue(RootCommand.CaptureProfileDelayOption));
        var startDebugSession = false;
        if (isExtensionHost)
        {
            startDebugSession = parseResult.GetValue(RootCommand.StartDebugSessionOption);
        }

        // Validate that --format is only used with --detach
        if (format == OutputFormat.Json && !detach)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, RunCommandStrings.FormatRequiresDetach);
        }

        // Validate that --no-build is not used when watch mode would be enabled.
        // The extension terminal path enables watch mode by delegating to VS Code
        // before an Aspire debug session exists. Once VS Code starts the session,
        // the child CLI has ASPIRE_EXTENSION_DEBUG_SESSION_ID and can honor
        // forwarded options from the original terminal command without recursing.
        var extensionTerminalRunWithoutDebugSession = isExtensionHost
            && !startDebugSession
            && string.IsNullOrEmpty(_configuration[KnownConfigNames.ExtensionDebugSessionId]);
        var watchModeEnabled = _features.IsFeatureEnabled(KnownFeatures.DefaultWatchEnabled, defaultValue: false) || extensionTerminalRunWithoutDebugSession;
        if (noBuild && watchModeEnabled)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, RunCommandStrings.NoBuildNotSupportedWithWatchMode);
        }

        if (!AppHostStartupTimeout.TryGetTimeoutSeconds(_configuration, InteractionService, out var timeoutSeconds))
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        // Handle detached mode - spawn child process and exit
        if (detach)
        {
            return await ExecuteDetachedAsync(parseResult, passedAppHostProjectFile, isExtensionHost, timeoutSeconds, cancellationToken);
        }

        // A user may run `aspire run` in an Aspire terminal in VS Code. In this case, intercept and prompt
        // VS Code to start a debug session using the current directory.
        // Skip this when running in non-interactive mode (e.g. as a child of `aspire start`)
        // to avoid delegating back to the extension instead of launching the AppHost directly.
        var nonInteractive = parseResult.GetValue(RootCommand.NonInteractiveOption);
        if (!nonInteractive
            && ExtensionHelper.IsExtensionHost(InteractionService, out var extensionInteractionService, out _)
            && string.IsNullOrEmpty(_configuration[KnownConfigNames.ExtensionDebugSessionId]))
        {
            extensionInteractionService.DisplayConsolePlainText(string.Format(CultureInfo.CurrentCulture, startDebugSession ? RunCommandStrings.StartingDebugSessionInExtension : RunCommandStrings.StartingRunSessionInExtension, "run"));
            await extensionInteractionService.StartDebugSessionAsync(ExecutionContext.WorkingDirectory.FullName, passedAppHostProjectFile?.FullName, startDebugSession, new DebugSessionOptions { Command = "run" });
            return CommandResult.Success();
        }

        AppHostProjectContext? context = null;
        Activity? runActivity = null;

        try
        {
            // Start a reported telemetry activity for the app host run early so that
            // all failure paths (project not found, incompatible version, etc.) are captured.
            runActivity = Telemetry.StartReportedActivity(name: TelemetryConstants.Activities.RunAppHost);
            runActivity?.SetTag(TelemetryConstants.Tags.AppHostDetached, _configuration.GetBool(KnownConfigNames.CliRunDetached) is true);
            runActivity?.SetTag(TelemetryConstants.Tags.AppHostIsolated, isolated);

            using var activity = _profilingTelemetry.StartRunCommand();

            var multipleAppHostBehavior = _hostEnvironment.SupportsInteractiveInput
                ? MultipleAppHostProjectsFoundBehavior.Prompt
                : MultipleAppHostProjectsFoundBehavior.Throw;

            AppHostProjectSearchResult searchResult;
            using (var findAppHostActivity = _profilingTelemetry.StartRunAppHostFindAppHost(passedAppHostProjectFile))
            {
                searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(
                    passedAppHostProjectFile,
                    multipleAppHostBehavior,
                    createSettingsFile: true,
                    cancellationToken);
            }
            var effectiveAppHostFile = searchResult.SelectedProjectFile;

            if (effectiveAppHostFile is null)
            {
                runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "project_not_found");
                return CommandResult.Failure(CliExitCodes.FailedToFindProject);
            }

            // Resolve the language for this file and get the appropriate handler
            var project = _projectFactory.TryGetProject(effectiveAppHostFile);
            if (project is null)
            {
                runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "project_not_found");
                return CommandResult.Failure(CliExitCodes.FailedToFindProject, "Unrecognized app host type.");
            }

            runActivity?.SetTag(TelemetryConstants.Tags.AppHostLanguage, project.LanguageId);

            // Check for running instance — even if we fail to stop we won't
            // block the apphost starting to make sure we don't ever break flow.
            // It should mostly stop just fine though.
            RunningInstanceResult runningInstanceResult;
            using (var stopRunningInstanceActivity = _profilingTelemetry.StartRunAppHostStopExistingInstance())
            {
                runningInstanceResult = await project.FindAndStopRunningInstanceAsync(effectiveAppHostFile, ExecutionContext.HomeDirectory, cancellationToken);
                stopRunningInstanceActivity.SetAppHostRunningInstanceResult(runningInstanceResult);
            }

            // If in isolated mode and a running instance was stopped, warn the user
            if (isolated && runningInstanceResult == RunningInstanceResult.InstanceStopped)
            {
                InteractionService.DisplayMessage(KnownEmojis.Warning, RunCommandStrings.IsolatedModeRunningInstanceWarning);
            }

            // The completion sources are the contract between RunCommand and IAppHostProject
            var buildCompletionSource = new TaskCompletionSource<bool>();
            var backchannelCompletionSource = new TaskCompletionSource<IAppHostCliBackchannel>();
            var waitForDebugger = parseResult.GetValue(RootCommand.WaitForDebuggerOption);

            context = new AppHostProjectContext
            {
                AppHostFile = effectiveAppHostFile,
                Watch = false,
                Debug = parseResult.GetValue(RootCommand.DebugOption),
                NoBuild = noBuild,
                NoRestore = noBuild, // --no-build implies --no-restore
                WaitForDebugger = waitForDebugger,
                Isolated = isolated,
                StartDebugSession = startDebugSession,
                EnvironmentVariables = new Dictionary<string, string>(),
                UnmatchedTokens = parseResult.UnmatchedTokens.ToArray(),
                WorkingDirectory = ExecutionContext.WorkingDirectory,
                BuildCompletionSource = buildCompletionSource,
                BackchannelCompletionSource = backchannelCompletionSource,
            };
            ProfilingTelemetry.AddCurrentContextToEnvironment(context.EnvironmentVariables);
            if (captureProfile)
            {
                ProfileCaptureEnvironment.AddCurrentToEnvironment(context.EnvironmentVariables);
            }

            // Start the project run as a pending task - we'll handle UX while it runs
            Task<int> pendingRun;
            var startupTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            var startupStartTimestamp = _timeProvider.GetTimestamp();
            using var runCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using (_profilingTelemetry.StartRunAppHostStartProject(project.LanguageId, noBuild, waitForDebugger))
            {
                pendingRun = project.RunAsync(context, runCancellationTokenSource.Token);
            }

            // Wait for the build to complete first (project handles its own build status spinners)
            bool buildSuccess;
            using (var waitForBuildActivity = _profilingTelemetry.StartRunAppHostWaitForBuild())
            {
                try
                {
                    buildSuccess = await buildCompletionSource.Task.WaitAsync(GetRemainingStartupTimeout(startupStartTimestamp, startupTimeout), _timeProvider, cancellationToken);
                }
                catch (TimeoutException)
                {
                    runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "startup_timeout");
                    await CancelAppHostStartupAsync(runCancellationTokenSource, pendingRun, cancellationToken).ConfigureAwait(false);
                    return CreateStartupTimeoutResult(timeoutSeconds);
                }

                waitForBuildActivity.SetAppHostBuildSuccess(buildSuccess);
            }
            if (!buildSuccess)
            {
                runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "build_failed");
                // Build failed - display captured output and return exit code
                if (context.OutputCollector is { } outputCollector)
                {
                    InteractionService.DisplayLines(outputCollector.GetLines());
                }
                return CommandResult.Failure(await pendingRun, InteractionServiceStrings.ProjectCouldNotBeBuilt);
            }
            var appHostStartupOutputStartIndex = context.OutputCollector?.GetLines().Count() ?? 0;

            // If --wait-for-debugger, display a message so the user knows the AppHost is paused.
            if (waitForDebugger)
            {
                InteractionService.DisplayMessage(KnownEmojis.Bug, InteractionServiceStrings.WaitingForDebuggerToAttachToAppHost);
            }

            using var logCaptureCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pendingLogCapture = Task.CompletedTask;

            try
            {
                AppHostStartupResult startup;
                try
                {
                    startup = await WaitForAppHostStartupAsync(
                        pendingRun,
                        backchannelCompletionSource,
                        logCaptureCancellationSource,
                        context.OutputCollector,
                        appHostStartupOutputStartIndex,
                        startupStartTimestamp,
                        startupTimeout,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "startup_timeout");
                    await CancelAppHostStartupAsync(runCancellationTokenSource, pendingRun, cancellationToken).ConfigureAwait(false);
                    return CreateStartupTimeoutResult(timeoutSeconds);
                }

                var backchannel = startup.Backchannel;
                var dashboardUrls = startup.DashboardUrls;
                pendingLogCapture = startup.PendingLogCapture;

                if (dashboardUrls.DashboardHealthy is false)
                {
                    InteractionService.DisplayMessage(KnownEmojis.Warning, RunCommandStrings.DashboardFailedToStart);
                }

                // Display the UX
                var appHostRelativePath = Path.GetRelativePath(ExecutionContext.WorkingDirectory.FullName, effectiveAppHostFile.FullName);
                var longestLocalizedLengthWithColon = RenderAppHostSummary(
                    InteractionService,
                    appHostRelativePath,
                    dashboardUrls.BaseUrlWithLoginToken,
                    dashboardUrls.CodespacesUrlWithLoginToken,
                    _fileLoggerProvider.LogFilePath,
                    isExtensionHost);

                if (ExtensionHelper.IsExtensionHost(InteractionService, out var extInteractionService, out _))
                {
                    if (dashboardUrls.DashboardHealthy is true)
                    {
                        extInteractionService.DisplayDashboardUrls(dashboardUrls);
                    }

                    extInteractionService.NotifyAppHostStartupCompleted();
                }

                // Handle remote environments (Codespaces, Remote Containers, SSH)
                var isCodespaces = dashboardUrls.CodespacesUrlWithLoginToken is not null;
                var isRemoteContainers = string.Equals(_configuration["REMOTE_CONTAINERS"], "true", StringComparison.OrdinalIgnoreCase);
                var isSshRemote = _configuration["VSCODE_IPC_HOOK_CLI"] is not null
                                  && _configuration["SSH_CONNECTION"] is not null;
                var isRemoteEnvironment = isCodespaces || isRemoteContainers || isSshRemote;

                var profileStopRequested = false;
                if (captureProfile)
                {
                    profileStopRequested = await RequestAppHostStopForProfileAsync(backchannel, pendingRun, captureProfileDelay, _profilingTelemetry, cancellationToken).ConfigureAwait(false);
                }
                else if (!isRemoteEnvironment)
                {
                    AppendCtrlCMessage(longestLocalizedLengthWithColon);
                }
                else
                {
                    // We want to display resource information in remote environments.
                    // Resources update over time so we'll use a live display.
                    // It is used to show discovered endpoints as they come in over the backchannel.
                    var discoveredEndpoints = new List<(string Resource, string Endpoint)>();
                    var endpointsLocalizedString = RunCommandStrings.Endpoints;
                    var showCtrlC = !ExtensionHelper.IsExtensionHost(InteractionService, out _, out _);

                    IRenderable BuildLiveRenderable()
                    {
                        var rows = new List<IRenderable>();

                        if (discoveredEndpoints.Count > 0)
                        {
                            var endpointsGrid = new Grid();
                            endpointsGrid.AddColumn();
                            endpointsGrid.AddColumn();
                            endpointsGrid.Columns[0].Width = longestLocalizedLengthWithColon;
                            endpointsGrid.AddRow(Text.Empty, Text.Empty);

                            for (var i = 0; i < discoveredEndpoints.Count; i++)
                            {
                                var (resource, endpoint) = discoveredEndpoints[i];
                                endpointsGrid.AddRow(
                                    i == 0
                                        ? new Align(new Markup($"[bold green]{endpointsLocalizedString}[/]:"), HorizontalAlignment.Right)
                                        : Text.Empty,
                                    new Markup($"[bold]{resource.EscapeMarkup()}[/] [grey]has endpoint[/] {MarkupHelpers.SafeLink(InteractionService, endpoint)}")
                                );
                            }

                            rows.Add(new Padder(endpointsGrid, new Padding(3, 0)));
                        }

                        if (showCtrlC)
                        {
                            rows.Add(BuildCtrlCRenderable(longestLocalizedLengthWithColon));
                        }

                        return rows.Count > 0 ? new Rows(rows) : Text.Empty;
                    }

                    try
                    {
                        await InteractionService.DisplayLiveAsync(BuildLiveRenderable(), async updateTarget =>
                        {
                            var resourceStates = backchannel.GetResourceStatesAsync(cancellationToken);
                            await foreach (var resourceState in resourceStates.WithCancellation(cancellationToken))
                            {
                                ProcessResourceState(resourceState, (resource, endpoint) =>
                                {
                                    discoveredEndpoints.Add((resource, endpoint));
                                    updateTarget(BuildLiveRenderable());
                                });
                            }
                        });
                    }
                    catch (ConnectionLostException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Orderly shutdown
                    }
                }

                using (var lifetimeActivity = _profilingTelemetry.StartRunAppHostLifetime())
                {
                    runActivity?.Stop();

                    try
                    {
                        await pendingLogCapture;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to capture logs from AppHost");
                        InteractionService.DisplayMessage(KnownEmojis.Warning, "No longer receiving logs from AppHost.");
                    }
                    finally
                    {
                        pendingLogCapture = Task.CompletedTask;
                    }

                    var exitCode = await pendingRun;
                    lifetimeActivity.SetProcessExitCode(exitCode);

                    // Capture mode intentionally turns a long-running AppHost startup into a finite command.
                    // Some AppHost implementations, including guest AppHosts, report the teardown exit code
                    // from a helper process that the CLI stops after the AppHost has already started; on
                    // Unix-like systems that surfaces as 128 + signal (e.g., 130 SIGINT, 137 SIGKILL, 143
                    // SIGTERM). These are AppHost process exit codes (not CLI exit codes), so use the raw
                    // signal-based literals here instead of CLI exit-code constants. Treat the known teardown
                    // codes as a successful capture, but propagate any other non-zero exit code so a
                    // genuine AppHost crash during shutdown is not masked.
                    if (profileStopRequested)
                    {
                        return exitCode is 0 or 130 or 137 or 143
                            ? CommandResult.Success()
                            : CommandResult.FromExitCode(exitCode);
                    }

                    // Cancelled by user (e.g., Ctrl+C) - treat as successful exit since the user intentionally stopped the AppHost.
                    return exitCode == CliExitCodes.Cancelled
                        ? CommandResult.Cancelled(CliExitCodes.Success)
                        : CommandResult.FromExitCode(exitCode);
                }
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == runCancellationTokenSource.Token && cancellationToken.IsCancellationRequested)
            {
                runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "canceled");

                // The user cancelled (e.g. Ctrl+C); the linked CTS we passed to project.RunAsync
                // propagated the cancellation and the OCE bubbled out with the linked token.
                // Treat as successful exit since the user intentionally stopped the AppHost.
                return CommandResult.Cancelled(CliExitCodes.Success);
            }
            finally
            {
                logCaptureCancellationSource.Cancel();
                try
                {
                    await pendingLogCapture.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AppHost log capture ended while the run command was exiting early.");
                }
            }
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken || ex is ExtensionOperationCanceledException)
        {
            runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "canceled");

            // Command is designed to be cancellable by the user (e.g. Ctrl+C) at any time.
            // Treat cancellation as a successful exit since the user intentionally stopped the AppHost.
            return CommandResult.Cancelled(CliExitCodes.Success);
        }
        catch (ProjectLocatorException ex)
        {
            runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "project_not_found");
            return HandleProjectLocatorException(ex, InteractionService, Telemetry);
        }
        catch (AppHostIncompatibleException ex)
        {
            runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "incompatible_version");
            Telemetry.RecordError(ex.Message, ex);
            return CommandResult.FromExitCode(InteractionService.DisplayIncompatibleVersionError(ex, ex.AspireHostingVersion ?? ex.RequiredCapability));
        }
        catch (CertificateServiceException ex)
        {
            runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "certificate_trust_failed");
            var errorMessage = string.Format(CultureInfo.CurrentCulture, TemplatingStrings.CertificateTrustError, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            return CommandResult.Failure(CliExitCodes.FailedToTrustCertificates, errorMessage);
        }
        catch (FailedToConnectBackchannelConnection ex)
        {
            runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "backchannel_connection_failed");
            var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ErrorConnectingToAppHost, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            return CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost, errorMessage);
        }
        catch (ConnectionLostException) when (isExtensionHost)
        {
            // When the extension manages the AppHost lifecycle (e.g., VS Code debug session),
            // it terminates the process on stop/restart, causing the backchannel to drop.
            return CommandResult.Success();
        }
        catch (AppHostExitedDuringStartupException ex)
        {
            return CreateRunExitResult(ex.ExitCode, ex.FailureMessage);
        }
        catch (Exception ex)
        {
            runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, ex.GetType().FullName);
            var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            return CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost, errorMessage);
        }
        finally
        {
            runActivity?.Dispose();
        }
    }

    private bool IsDetachedStartChild() => _configuration.GetBool(KnownConfigNames.CliRunDetached) is true;

    private static void DisplayRecentAppHostStartupOutput(IInteractionService interactionService, OutputCollector? outputCollector, int startupOutputStartIndex)
    {
        var outputLines = outputCollector?.GetLines()
            .Skip(startupOutputStartIndex)
            .Where(static line => line.Stream == OutputLineStream.StdErr)
            .TakeLast(MaxDisplayedAppHostStartupOutputLines)
            .ToArray();
        if (outputLines is null || outputLines.Length == 0)
        {
            return;
        }

        interactionService.DisplayMessage(KnownEmojis.Information, $"{RunCommandStrings.RecentAppHostStartupOutput}:");
        interactionService.DisplayLines(outputLines);
    }

    private static CommandResult CreateRunExitResult(int exitCode, string? errorMessage = null)
    {
        if (exitCode == CliExitCodes.Cancelled)
        {
            return CommandResult.Cancelled(CliExitCodes.Success);
        }

        return errorMessage is null
            ? CommandResult.FromExitCode(exitCode)
            : CommandResult.Failure(exitCode, errorMessage);
    }

    private static async Task<int?> ObserveEarlyDetachedStartupExitAsync(Task<int> pendingRun, CancellationToken cancellationToken)
    {
        var completedTask = await Task.WhenAny(
            pendingRun,
            Task.Delay(s_startupFailureObservationWindow, cancellationToken)).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (completedTask == pendingRun)
        {
            return await GetAppHostStartupExitCodeAsync(pendingRun).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<AppHostExitResolution> ResolveAppHostExitCodeAsync(Task<int> appHostFailureTask, CancellationToken cancellationToken)
    {
        try
        {
            var exitCode = await (cancellationToken.CanBeCanceled
                ? appHostFailureTask.WaitAsync(cancellationToken)
                : appHostFailureTask).ConfigureAwait(false);
            return new AppHostExitResolution(exitCode, FaultException: null);
        }
        catch (OperationCanceledException)
        {
            // Honor user-initiated cancellation by propagating; callers expect to see it.
            throw;
        }
        catch (Exception ex)
        {
            // appHostFailureTask faulted instead of returning a clean exit code (e.g. an unexpected
            // exception bubbled out of project.RunAsync). Treat that as a generic AppHost failure
            // so the caller can still display captured output and surface the failure uniformly,
            // and carry the fault exception forward so the caller can wrap it with the localized
            // UnexpectedErrorOccurred template for display alongside any captured output.
            return new AppHostExitResolution(CliExitCodes.FailedToDotnetRunAppHost, FaultException: ex);
        }
    }

    private readonly record struct AppHostExitResolution(int ExitCode, Exception? FaultException);

    private static void ObserveFaults(Task task)
    {
        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<int> GetAppHostStartupExitCodeAsync(Task<int> pendingRun)
    {
        try
        {
            return await pendingRun.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AppHostExitedDuringStartupException(CliExitCodes.FailedToDotnetRunAppHost, ex);
        }
    }

    private async Task<AppHostStartupResult> WaitForAppHostStartupAsync(
        Task<int> pendingRun,
        TaskCompletionSource<IAppHostCliBackchannel> backchannelCompletionSource,
        CancellationTokenSource logCaptureCancellationSource,
        OutputCollector? outputCollector,
        int appHostStartupOutputStartIndex,
        long startupStartTimestamp,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var happyPathTask = RunStartupHappyPathAsync(backchannelCompletionSource, logCaptureCancellationSource, pendingRun, startupStartTimestamp, startupTimeout, startupCts.Token);

        // Race the startup readiness signal against the AppHost system task. The AppHost
        // system is owned by the project and tears itself down (via an internal escalation
        // CTS that listens for BackchannelCompletionSource faults) whenever the server or
        // guest dies, so once happyPathTask faults the AppHost is on its way down too.
        if (await Task.WhenAny(happyPathTask, pendingRun).ConfigureAwait(false) == pendingRun)
        {
            ObserveFaults(happyPathTask);
            await startupCts.CancelAsync().ConfigureAwait(false);
            // pendingRun is already complete (it won the race), so there is nothing for a
            // cancellation token to interrupt - pass CancellationToken.None explicitly.
            var resolution = await ResolveAppHostExitCodeAsync(pendingRun, CancellationToken.None).ConfigureAwait(false);
            DisplayRecentAppHostStartupOutput(InteractionService, outputCollector, appHostStartupOutputStartIndex);
            // If the AppHost faulted (e.g. an unexpected exception bubbled out of RunAsync
            // before it could return an exit code), wrap the fault reason with the localized
            // UnexpectedErrorOccurred template so the user sees a consistent
            // "An unexpected error occurred: <reason>" line. When the AppHost returned a
            // real exit code we already have whatever output it produced and don't need a
            // generic wrapper - the captured output is the narrative.
            var failureMessage = resolution.FaultException is { } faultException
                ? string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, faultException.Message)
                : null;
            throw new AppHostExitedDuringStartupException(resolution.ExitCode, resolution.FaultException, failureMessage);
        }

        try
        {
            return await happyPathTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == startupCts.Token && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (AppHostExitedDuringStartupException)
        {
            // Intentional detached-startup signal from RunStartupHappyPathAsync: the AppHost
            // exited cleanly during the detached-start early-exit observation window. The
            // exit code and captured AppHost output already describe the outcome, so let it
            // propagate as-is and avoid wrapping it with the generic "unexpected error"
            // template (the AppHost exit isn't really unexpected at this point).
            DisplayRecentAppHostStartupOutput(InteractionService, outputCollector, appHostStartupOutputStartIndex);
            throw;
        }
        catch (TimeoutException)
        {
            // Bubble startup-timeout signal up to ExecuteAsync so it can cancel the run
            // and emit the localized timeout guidance. Must not be wrapped by the generic
            // catch below.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failureMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message);
            DisplayRecentAppHostStartupOutput(InteractionService, outputCollector, appHostStartupOutputStartIndex);

            if (AppHostFollowDisconnectHelpers.IsExpectedDisconnect(ex))
            {
                // The backchannel connection itself died, so the AppHost is dying too. Wait for it
                // to exit so we can surface its real exit code/captured output rather than a
                // generic CLI-side failure. BackchannelCompletionSource is already RanToCompletion
                // here so the project's escalation hook can't tear things down for us; show a
                // status that tells the user how to break out of the wait.
                var resolution = await InteractionService.ShowStatusAsync(
                    RunCommandStrings.AppHostConnectionLostWaitingForExit,
                    () => ResolveAppHostExitCodeAsync(pendingRun, cancellationToken)).ConfigureAwait(false);
                throw new AppHostExitedDuringStartupException(resolution.ExitCode, ex, failureMessage);
            }

            // Server-side RPC handler faulted (e.g. GetDashboardUrlsAsync threw) but the channel
            // is still alive and the AppHost may keep running indefinitely. The RPC fault payload
            // is already the real cause, so surface it immediately and let normal command teardown
            // shut the AppHost down. This mirrors pre-PR behavior for these failures.
            throw new AppHostExitedDuringStartupException(CliExitCodes.FailedToDotnetRunAppHost, ex, failureMessage);
        }
    }

    private async Task<AppHostStartupResult> RunStartupHappyPathAsync(
        TaskCompletionSource<IAppHostCliBackchannel> backchannelCompletionSource,
        CancellationTokenSource logCaptureCancellationSource,
        Task<int> pendingRun,
        long startupStartTimestamp,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        IAppHostCliBackchannel backchannel;
        using (var waitForBackchannelActivity = _profilingTelemetry.StartRunAppHostWaitForBackchannel())
        {
            backchannel = await InteractionService.ShowStatusAsync(
                RunCommandStrings.ConnectingToAppHost,
                async () => await backchannelCompletionSource.Task.WaitAsync(GetRemainingStartupTimeout(startupStartTimestamp, startupTimeout), _timeProvider, cancellationToken).ConfigureAwait(false));
            waitForBackchannelActivity.SetAppHostBackchannelConnected(true);
        }

        // Start log capture early so any output produced while we wait for dashboard URLs is
        // routed into the unified CLI log file. The task is returned to the caller so the run
        // command can await/cancel it during teardown.
        var pendingLogCapture = CaptureAppHostLogsAsync(_fileLoggerProvider, backchannel, InteractionService, logCaptureCancellationSource.Token);
        // Observe faults in case the caller never gets to await it - e.g., if a subsequent
        // step in this method throws, the local task goes out of scope without being returned
        // through AppHostStartupResult. CaptureAppHostLogsAsync already handles OCE and
        // ConnectionLostException-during-cancellation cleanly, but any other failure (or a
        // ConnectionLostException that fires before the outer finally cancels log capture) would
        // otherwise surface as an unobserved task exception.
        ObserveFaults(pendingLogCapture);

        DashboardUrlsState dashboardUrls;
        using (var getDashboardUrlsActivity = _profilingTelemetry.StartRunAppHostGetDashboardUrls())
        {
            dashboardUrls = await InteractionService.ShowStatusAsync(
                RunCommandStrings.StartingDashboard,
                async () => await backchannel.GetDashboardUrlsAsync(cancellationToken).ConfigureAwait(false));
            getDashboardUrlsActivity.SetAppHostDashboardHealthy(dashboardUrls.DashboardHealthy);
        }

        if (IsDetachedStartChild())
        {
            var observedExitCode = await ObserveEarlyDetachedStartupExitAsync(pendingRun, cancellationToken).ConfigureAwait(false);
            if (observedExitCode is { } exitCode)
            {
                throw new AppHostExitedDuringStartupException(exitCode);
            }
        }

        await backchannel.NotifyAppHostReadyAsync(cancellationToken).ConfigureAwait(false);

        return new AppHostStartupResult(backchannel, dashboardUrls, pendingLogCapture);
    }

    private sealed record AppHostStartupResult(
        IAppHostCliBackchannel Backchannel,
        DashboardUrlsState DashboardUrls,
        Task PendingLogCapture);

    private sealed class AppHostExitedDuringStartupException(int exitCode, Exception? innerException = null, string? failureMessage = null) : Exception("The AppHost exited during startup.", innerException)
    {
        public int ExitCode { get; } = exitCode;

        /// <summary>
        /// Optional user-facing message describing why startup failed. Populated when the failure
        /// originated from the CLI-side startup happy path (e.g., a backchannel timeout or RPC
        /// fault) rather than from output the AppHost itself wrote to stderr, so that the message
        /// can be surfaced through the normal command-result error path.
        /// </summary>
        public string? FailureMessage { get; } = failureMessage;
    }

    private static IRenderable BuildCtrlCRenderable(int longestLocalizedLengthWithColon)
    {
        var ctrlCGrid = new Grid();
        ctrlCGrid.AddColumn();
        ctrlCGrid.AddColumn();
        ctrlCGrid.Columns[0].Width = longestLocalizedLengthWithColon;
        ctrlCGrid.AddRow(Text.Empty, Text.Empty);
        ctrlCGrid.AddRow(new Text(string.Empty), new Markup(RunCommandStrings.PressCtrlCToStopAppHost) { Overflow = Overflow.Ellipsis });

        return new Padder(ctrlCGrid, new Padding(3, 0));
    }

    private void AppendCtrlCMessage(int longestLocalizedLengthWithColon)
    {
        if (ExtensionHelper.IsExtensionHost(InteractionService, out _, out _))
        {
            return;
        }

        InteractionService.DisplayRenderable(BuildCtrlCRenderable(longestLocalizedLengthWithColon));
    }

    private static async Task<bool> RequestAppHostStopForProfileAsync(
        IAppHostCliBackchannel backchannel,
        Task<int> pendingRun,
        TimeSpan delay,
        ProfilingTelemetry profilingTelemetry,
        CancellationToken cancellationToken)
    {
        // The AppHost exports profiling spans through the batched OTLP exporter. Keep the process
        // alive briefly after startup so late server-side spans (for example dashboard readiness)
        // have time to flush before the CLI requests shutdown and exports the capture archive.
        if (delay > TimeSpan.Zero)
        {
            using (profilingTelemetry.StartProfileCaptureDelay(delay))
            {
                var delayTask = Task.Delay(delay, cancellationToken);
                var completedTask = await Task.WhenAny(delayTask, pendingRun).ConfigureAwait(false);
                if (completedTask == pendingRun)
                {
                    return false;
                }

                await delayTask.ConfigureAwait(false);
            }
        }

        if (!pendingRun.IsCompleted)
        {
            await backchannel.RequestStopAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Renders the AppHost summary grid with AppHost path, dashboard URL, logs path, and optionally PID.
    /// </summary>
    /// <param name="console">The console to write to.</param>
    /// <param name="appHostRelativePath">The relative path to the AppHost file.</param>
    /// <param name="dashboardUrl">The dashboard URL with login token, or null if not available.</param>
    /// <param name="codespacesUrl">The codespaces URL with login token, or null if not in codespaces.</param>
    /// <param name="logFilePath">The full path to the log file.</param>
    /// <param name="pid">The process ID to display, or null to omit the PID row.</param>
    /// <param name="isExtensionHost">Whether the AppHost is running in the Aspire extension.</param>
    /// <returns>The column width used, for subsequent grid additions.</returns>
    internal static int RenderAppHostSummary(
        IInteractionService console,
        string appHostRelativePath,
        string? dashboardUrl,
        string? codespacesUrl,
        string logFilePath,
        bool isExtensionHost,
        int? pid = null)
    {
        console.DisplayEmptyLine();
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();

        var appHostLabel = RunCommandStrings.AppHost;
        var dashboardLabel = RunCommandStrings.Dashboard;
        var logsLabel = RunCommandStrings.Logs;
        var pidLabel = RunCommandStrings.ProcessId;

        // Calculate column width based on labels that will actually be displayed
        var labels = new List<string> { appHostLabel, logsLabel };
        if (!isExtensionHost)
        {
            labels.Add(dashboardLabel);
        }
        if (pid.HasValue)
        {
            labels.Add(pidLabel);
        }
        var longestLabelLength = labels.Max(s => s.Length) + 1; // +1 for colon

        grid.Columns[0].Width = longestLabelLength;

        // In the extension's debug console, right-aligned labels and the surrounding padding
        // render as visible left indentation, and the empty separator rows show up as blank
        // lines that just push real content further down. Use a flush, single-spaced layout
        // for the extension and keep the spaced-out look only for direct terminal output.
        IRenderable LabelMarkup(string label)
        {
            var markup = new Markup($"[bold green]{label}[/]:");
            return isExtensionHost ? markup : new Align(markup, HorizontalAlignment.Right);
        }

        // AppHost row
        grid.AddRow(LabelMarkup(appHostLabel), new Text(appHostRelativePath));
        if (!isExtensionHost)
        {
            grid.AddRow(Text.Empty, Text.Empty);
        }

        if (!isExtensionHost)
        {
            // Dashboard row
            if (!string.IsNullOrEmpty(dashboardUrl))
            {
                grid.AddRow(
                    LabelMarkup(dashboardLabel),
                    new Markup(MarkupHelpers.SafeLink(console, dashboardUrl)));

                // Codespaces URL (if available)
                if (!string.IsNullOrEmpty(codespacesUrl))
                {
                    grid.AddRow(Text.Empty, new Markup(MarkupHelpers.SafeLink(console, codespacesUrl)));
                }
            }
            else
            {
                grid.AddRow(
                    LabelMarkup(dashboardLabel),
                    new Markup("[dim]N/A[/]"));
            }
            grid.AddRow(Text.Empty, Text.Empty);
        }

        // Logs row
        grid.AddRow(LabelMarkup(logsLabel), new Markup(MarkupHelpers.SafeFileLink(console, logFilePath)));

        // PID row (if provided)
        if (pid.HasValue)
        {
            if (!isExtensionHost)
            {
                grid.AddRow(Text.Empty, Text.Empty);
            }
            grid.AddRow(LabelMarkup(pidLabel), new Text(pid.Value.ToString(CultureInfo.InvariantCulture)));
        }

        IRenderable summary = isExtensionHost ? grid : new Padder(grid, new Padding(3, 0));
        console.DisplayRenderable(summary);

        return longestLabelLength;
    }

    internal static async Task CaptureAppHostLogsAsync(FileLoggerProvider fileLoggerProvider, IAppHostCliBackchannel backchannel, IInteractionService interactionService, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();

            var logEntries = backchannel.GetAppHostLogEntriesAsync(cancellationToken);

            await foreach (var entry in logEntries.WithCancellation(cancellationToken))
            {
                if (ExtensionHelper.IsExtensionHost(interactionService, out var extensionInteractionService, out _))
                {
                    if (entry.LogLevel is not LogLevel.Trace and not LogLevel.Debug)
                    {
                        // Send only information+ level logs to the extension host.
                        extensionInteractionService.WriteDebugSessionMessage(entry.Message, entry.LogLevel is not LogLevel.Error and not LogLevel.Critical, "\x1b[2m");
                    }
                }

                // Write to the unified log file via FileLoggerProvider
                var shortCategory = FileLoggerProvider.GetShortCategoryName(entry.CategoryName);
                fileLoggerProvider.WriteLog(entry.Timestamp, entry.LogLevel, $"AppHost/{shortCategory}", entry.Message);
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

    /// <summary>
    /// Executes the run command in detached mode by spawning a child CLI process.
    /// The parent waits for the auxiliary backchannel to become available, displays a summary, then exits
    /// while the child continues running.
    /// </summary>
    /// <remarks>
    /// <para><b>Failure Modes:</b></para>
    /// <list type="number">
    /// <item><b>Project not found</b>: No AppHost project found in the current directory or specified path.
    /// Returns <see cref="CliExitCodes.FailedToFindProject"/>.</item>
    /// <item><b>Failed to spawn child process</b>: Process.Start fails (e.g., executable not found).
    /// Returns <see cref="CliExitCodes.FailedToDotnetRunAppHost"/>.</item>
    /// <item><b>Child process exits early</b>: The child 'aspire run' process exits before the backchannel
    /// is established (e.g., build failure, configuration error). Detected via WaitForExitAsync racing
    /// with the poll delay. Shows exit code and log file path.
    /// Returns <see cref="CliExitCodes.FailedToDotnetRunAppHost"/>.</item>
    /// <item><b>Timeout waiting for backchannel</b>: The auxiliary backchannel socket doesn't appear
    /// within the configured startup timeout. The child process is killed. Shows timeout message and log file path.
    /// Returns <see cref="CliExitCodes.FailedToDotnetRunAppHost"/>.</item>
    /// </list>
    /// <para>On any failure, the log file path is displayed so the user can investigate.</para>
    /// </remarks>
    private Task<CommandResult> ExecuteDetachedAsync(ParseResult parseResult, FileInfo? passedAppHostProjectFile, bool isExtensionHost, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var format = parseResult.GetValue(AppHostLauncher.s_formatOption);
        var isolated = parseResult.GetValue(AppHostLauncher.s_isolatedOption);
        var noBuild = parseResult.GetValue(s_noBuildOption);
        var waitForDebugger = parseResult.GetValue(RootCommand.WaitForDebuggerOption);
        var globalArgs = RootCommand.GetChildProcessArgs(parseResult);
        var additionalArgs = parseResult.UnmatchedTokens.Where(t => t != "--detach").ToList();
        var captureProfile = parseResult.GetValue(RootCommand.CaptureProfileOption);
        var stopAfterLaunchDelay = captureProfile
            ? TimeSpan.FromSeconds(parseResult.GetValue(RootCommand.CaptureProfileDelayOption))
            : (TimeSpan?)null;

        if (noBuild)
        {
            additionalArgs.Add("--no-build");
        }

        return _appHostLauncher.LaunchDetachedAsync(
            passedAppHostProjectFile,
            format,
            isolated,
            isExtensionHost,
            waitForDebugger,
            timeoutSeconds,
            globalArgs,
            additionalArgs,
            stopAfterLaunchDelay,
            cancellationToken);
    }

    private TimeSpan GetRemainingStartupTimeout(long startupStartTimestamp, TimeSpan startupTimeout)
    {
        var elapsed = _timeProvider.GetElapsedTime(startupStartTimestamp);
        return elapsed >= startupTimeout ? TimeSpan.Zero : startupTimeout - elapsed;
    }

    private async Task CancelAppHostStartupAsync(CancellationTokenSource runCancellationTokenSource, Task<int> pendingRun, CancellationToken cancellationToken)
    {
        runCancellationTokenSource.Cancel();

        try
        {
            // The timeout is a safety net for the startup-timeout path (no Ctrl+C). When the user
            // presses Ctrl+C, cancellationToken fires and WaitAsync exits immediately via the token
            // rather than waiting for the full timeout duration.
            await pendingRun.WaitAsync(s_appHostStartupCancellationTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCancellationTokenSource.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
        }
        catch (TimeoutException ex)
        {
            _logger.LogDebug(ex, "Timed out waiting for AppHost startup cancellation to complete.");
            _ = ObserveAppHostRunFailureAsync(pendingRun);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AppHost run failed after startup cancellation.");
        }
    }

    private async Task ObserveAppHostRunFailureAsync(Task<int> pendingRun)
    {
        try
        {
            await pendingRun.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AppHost run failed after startup cancellation timeout.");
        }
    }

    private static CommandResult CreateStartupTimeoutResult(int timeoutSeconds)
    {
        return CommandResult.Failure(
            CliExitCodes.FailedToDotnetRunAppHost,
            string.Format(CultureInfo.CurrentCulture, RunCommandStrings.TimeoutWaitingForAppHost, timeoutSeconds, CliConfigNames.AppHostStartupTimeout));
    }

}
