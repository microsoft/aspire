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
    private readonly IInteractionService _interactionService;
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
    private bool _isDetachMode;

    protected override bool UpdateNotificationsEnabled => !_isDetachMode;

    private static readonly Option<bool> s_detachOption = new("--detach")
    {
        Description = RunCommandStrings.DetachArgumentDescription
    };
    private static readonly Option<bool> s_noBuildOption = new("--no-build")
    {
        Description = RunCommandStrings.NoBuildArgumentDescription
    };
    private readonly Option<bool>? _startDebugSessionOption;

    public RunCommand(
        IDotNetCliRunner runner,
        IInteractionService interactionService,
        ICertificateService certificateService,
        IProjectLocator projectLocator,
        AspireCliTelemetry telemetry,
        IConfiguration configuration,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        IServiceProvider serviceProvider,
        CliExecutionContext executionContext,
        ILogger<RunCommand> logger,
        IAppHostProjectFactory projectFactory,
        AppHostLauncher appHostLauncher,
        FileLoggerProvider fileLoggerProvider,
        ICliHostEnvironment hostEnvironment)
        : base("run", RunCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _runner = runner;
        _interactionService = interactionService;
        _certificateService = certificateService;
        _projectLocator = projectLocator;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _features = features;
        _logger = logger;
        _projectFactory = projectFactory;
        _appHostLauncher = appHostLauncher;
        _fileLoggerProvider = fileLoggerProvider;
        _hostEnvironment = hostEnvironment;

        Options.Add(s_detachOption);
        Options.Add(s_noBuildOption);
        AppHostLauncher.AddLaunchOptions(this);

        if (ExtensionHelper.IsExtensionHost(InteractionService, out _, out _))
        {
            _startDebugSessionOption = new Option<bool>("--start-debug-session")
            {
                Description = RunCommandStrings.StartDebugSessionArgumentDescription
            };
            Options.Add(_startDebugSessionOption);
        }

        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var passedAppHostProjectFile = parseResult.GetValue(AppHostLauncher.s_appHostOption);
        var detach = parseResult.GetValue(s_detachOption);
        _isDetachMode = detach;
        var noBuild = parseResult.GetValue(s_noBuildOption);
        var format = parseResult.GetValue(AppHostLauncher.s_formatOption);
        var isolated = parseResult.GetValue(AppHostLauncher.s_isolatedOption);
        var isExtensionHost = ExtensionHelper.IsExtensionHost(InteractionService, out _, out _);
        var startDebugSession = false;
        if (isExtensionHost)
        {
            Debug.Assert(_startDebugSessionOption is not null);
            startDebugSession = parseResult.GetValue(_startDebugSessionOption);
        }

        // Validate that --format is only used with --detach
        if (format == OutputFormat.Json && !detach)
        {
            InteractionService.DisplayError(RunCommandStrings.FormatRequiresDetach);
            return ExitCodeConstants.InvalidCommand;
        }

        // Validate that --no-build is not used when watch mode would be enabled
        // Watch mode is enabled when DefaultWatchEnabled feature is true, or when running under extension host (not in debug session)
        var watchModeEnabled = _features.IsFeatureEnabled(KnownFeatures.DefaultWatchEnabled, defaultValue: false) || (isExtensionHost && !startDebugSession);
        if (noBuild && watchModeEnabled)
        {
            InteractionService.DisplayError(RunCommandStrings.NoBuildNotSupportedWithWatchMode);
            return ExitCodeConstants.InvalidCommand;
        }

        // Handle detached mode - spawn child process and exit
        if (detach)
        {
            return await ExecuteDetachedAsync(parseResult, passedAppHostProjectFile, isExtensionHost, cancellationToken);
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
            extensionInteractionService.DisplayConsolePlainText(RunCommandStrings.StartingDebugSessionInExtension);
            await extensionInteractionService.StartDebugSessionAsync(ExecutionContext.WorkingDirectory.FullName, passedAppHostProjectFile?.FullName, startDebugSession, new DebugSessionOptions { Command = "run" });
            return ExitCodeConstants.Success;
        }

        AppHostProjectContext? context = null;
        Activity? runActivity = null;

        try
        {
            using var activity = Telemetry.StartDiagnosticActivity(this.Name);

            // Start a reported telemetry activity for the app host run early so that
            // all failure paths (project not found, incompatible version, etc.) are captured.
            runActivity = Telemetry.StartReportedActivity(name: TelemetryConstants.Activities.RunAppHost);
            runActivity?.SetTag(TelemetryConstants.Tags.AppHostDetached, _configuration.GetBool(KnownConfigNames.CliRunDetached) is true);
            runActivity?.SetTag(TelemetryConstants.Tags.AppHostIsolated, isolated);

            var multipleAppHostBehavior = _hostEnvironment.SupportsInteractiveInput
                ? MultipleAppHostProjectsFoundBehavior.Prompt
                : MultipleAppHostProjectsFoundBehavior.Throw;

            var searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(
                passedAppHostProjectFile,
                multipleAppHostBehavior,
                createSettingsFile: true,
                cancellationToken);
            var effectiveAppHostFile = searchResult.SelectedProjectFile;

            if (effectiveAppHostFile is null)
            {
                runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "project_not_found");
                return ExitCodeConstants.FailedToFindProject;
            }

            // Resolve the language for this file and get the appropriate handler
            var project = _projectFactory.TryGetProject(effectiveAppHostFile);
            if (project is null)
            {
                runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "project_not_found");
                InteractionService.DisplayError("Unrecognized app host type.");
                return ExitCodeConstants.FailedToFindProject;
            }

            runActivity?.SetTag(TelemetryConstants.Tags.AppHostLanguage, project.LanguageId);

            // Check for running instance — even if we fail to stop we won't
            // block the apphost starting to make sure we don't ever break flow.
            // It should mostly stop just fine though.
            var runningInstanceResult = await project.FindAndStopRunningInstanceAsync(effectiveAppHostFile, ExecutionContext.HomeDirectory, cancellationToken);

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

            // Start the project run as a pending task - we'll handle UX while it runs
            var pendingRun = project.RunAsync(context, cancellationToken);

            // Wait for the build to complete first (project handles its own build status spinners)
            var buildSuccess = await buildCompletionSource.Task.WaitAsync(cancellationToken);
            if (!buildSuccess)
            {
                runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "build_failed");
                // Build failed - display captured output and return exit code
                if (context.OutputCollector is { } outputCollector)
                {
                    InteractionService.DisplayLines(outputCollector.GetLines());
                }
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ProjectCouldNotBeBuilt, ExecutionContext.LogFilePath));
                return await pendingRun;
            }

            // If --wait-for-debugger, display a message so the user knows the AppHost is paused.
            if (waitForDebugger)
            {
                InteractionService.DisplayMessage(KnownEmojis.Bug, InteractionServiceStrings.WaitingForDebuggerToAttachToAppHost);
            }

            // Now wait for the backchannel to be established
            var backchannel = await InteractionService.ShowStatusAsync(
                RunCommandStrings.ConnectingToAppHost,
                async () => await backchannelCompletionSource.Task.WaitAsync(cancellationToken));

            // Set up log capture - writes to unified CLI log file
            var pendingLogCapture = CaptureAppHostLogsAsync(_fileLoggerProvider, backchannel, _interactionService, cancellationToken);

            // Get dashboard URLs
            var dashboardUrls = await InteractionService.ShowStatusAsync(
                RunCommandStrings.StartingDashboard,
                async () => await backchannel.GetDashboardUrlsAsync(cancellationToken));

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

            // Handle remote environments (Codespaces, Remote Containers, SSH)
            var isCodespaces = dashboardUrls.CodespacesUrlWithLoginToken is not null;
            var isRemoteContainers = string.Equals(_configuration["REMOTE_CONTAINERS"], "true", StringComparison.OrdinalIgnoreCase);
            var isSshRemote = _configuration["VSCODE_IPC_HOOK_CLI"] is not null
                              && _configuration["SSH_CONNECTION"] is not null;
            var isRemoteEnvironment = isCodespaces || isRemoteContainers || isSshRemote;

            if (!isRemoteEnvironment)
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
                var showCtrlC = !ExtensionHelper.IsExtensionHost(_interactionService, out _, out _);

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
                                new Markup($"[bold]{resource.EscapeMarkup()}[/] [grey]has endpoint[/] [link={endpoint.EscapeMarkup()}]{endpoint.EscapeMarkup()}[/]")
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

            if (ExtensionHelper.IsExtensionHost(InteractionService, out var extInteractionService, out _))
            {
                extInteractionService.DisplayDashboardUrls(dashboardUrls);
                extInteractionService.NotifyAppHostStartupCompleted();
            }

            await pendingLogCapture;
            return await pendingRun;
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken || ex is ExtensionOperationCanceledException)
        {
            runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "canceled");
            InteractionService.DisplayCancellationMessage();
            return ExitCodeConstants.Success;
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
            return InteractionService.DisplayIncompatibleVersionError(ex, ex.AspireHostingVersion ?? ex.RequiredCapability);
        }
        catch (CertificateServiceException ex)
        {
            runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "certificate_trust_failed");
            var errorMessage = string.Format(CultureInfo.CurrentCulture, TemplatingStrings.CertificateTrustError, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            InteractionService.DisplayError(errorMessage);
            return ExitCodeConstants.FailedToTrustCertificates;
        }
        catch (FailedToConnectBackchannelConnection ex)
        {
            runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, "backchannel_connection_failed");
            var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ErrorConnectingToAppHost, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            InteractionService.DisplayError(errorMessage);
            // Don't display raw output - it's already in the log file
            InteractionService.DisplayMessage(KnownEmojis.PageFacingUp, string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, ExecutionContext.LogFilePath));
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }
        catch (ConnectionLostException) when (isExtensionHost)
        {
            // When the extension manages the AppHost lifecycle (e.g., VS Code debug session),
            // it terminates the process on stop/restart, causing the backchannel to drop.
            return ExitCodeConstants.Success;
        }
        catch (Exception ex)
        {
            runActivity?.SetTag(TelemetryConstants.Tags.ErrorType, ex.GetType().FullName);
            var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            InteractionService.DisplayError(errorMessage);
            // Don't display raw output - it's already in the log file
            InteractionService.DisplayMessage(KnownEmojis.PageFacingUp, string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, ExecutionContext.LogFilePath));
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }
        finally
        {
            runActivity?.Dispose();
        }
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
        if (ExtensionHelper.IsExtensionHost(_interactionService, out _, out _))
        {
            return;
        }

        InteractionService.DisplayRenderable(BuildCtrlCRenderable(longestLocalizedLengthWithColon));
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

        // AppHost row
        grid.AddRow(
            new Align(new Markup($"[bold green]{appHostLabel}[/]:"), HorizontalAlignment.Right),
            new Text(appHostRelativePath));
        grid.AddRow(Text.Empty, Text.Empty);

        if (!isExtensionHost)
        {
            // Dashboard row
            if (!string.IsNullOrEmpty(dashboardUrl))
            {
                grid.AddRow(
                    new Align(new Markup($"[bold green]{dashboardLabel}[/]:"), HorizontalAlignment.Right),
                    new Markup($"[link={dashboardUrl}]{dashboardUrl}[/]"));

                // Codespaces URL (if available)
                if (!string.IsNullOrEmpty(codespacesUrl))
                {
                    grid.AddRow(Text.Empty, new Markup($"[link={codespacesUrl}]{codespacesUrl}[/]"));
                }
            }
            else
            {
                grid.AddRow(
                    new Align(new Markup($"[bold green]{dashboardLabel}[/]:"), HorizontalAlignment.Right),
                    new Markup("[dim]N/A[/]"));
            }
            grid.AddRow(Text.Empty, Text.Empty);
        }

        // Logs row
        grid.AddRow(
            new Align(new Markup($"[bold green]{logsLabel}[/]:"), HorizontalAlignment.Right),
            new Text(logFilePath));

        // PID row (if provided)
        if (pid.HasValue)
        {
            grid.AddRow(Text.Empty, Text.Empty);
            grid.AddRow(
                new Align(new Markup($"[bold green]{pidLabel}[/]:"), HorizontalAlignment.Right),
                new Text(pid.Value.ToString(CultureInfo.InvariantCulture)));
        }

        var padder = new Padder(grid, new Padding(3, 0));
        console.DisplayRenderable(padder);

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
    /// Returns <see cref="ExitCodeConstants.FailedToFindProject"/>.</item>
    /// <item><b>Failed to spawn child process</b>: Process.Start fails (e.g., executable not found).
    /// Returns <see cref="ExitCodeConstants.FailedToDotnetRunAppHost"/>.</item>
    /// <item><b>Child process exits early</b>: The child 'aspire run' process exits before the backchannel
    /// is established (e.g., build failure, configuration error). Detected via WaitForExitAsync racing
    /// with the poll delay. Shows exit code and log file path.
    /// Returns <see cref="ExitCodeConstants.FailedToDotnetRunAppHost"/>.</item>
    /// <item><b>Timeout waiting for backchannel</b>: The auxiliary backchannel socket doesn't appear
    /// within 120 seconds. The child process is killed. Shows timeout message and log file path.
    /// Returns <see cref="ExitCodeConstants.FailedToDotnetRunAppHost"/>.</item>
    /// </list>
    /// <para>On any failure, the log file path is displayed so the user can investigate.</para>
    /// </remarks>
    private Task<int> ExecuteDetachedAsync(ParseResult parseResult, FileInfo? passedAppHostProjectFile, bool isExtensionHost, CancellationToken cancellationToken)
    {
        var format = parseResult.GetValue(AppHostLauncher.s_formatOption);
        var isolated = parseResult.GetValue(AppHostLauncher.s_isolatedOption);
        var noBuild = parseResult.GetValue(s_noBuildOption);
        var waitForDebugger = parseResult.GetValue(RootCommand.WaitForDebuggerOption);
        var globalArgs = RootCommand.GetChildProcessArgs(parseResult);
        var additionalArgs = parseResult.UnmatchedTokens.Where(t => t != "--detach").ToList();

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
            globalArgs,
            additionalArgs,
            cancellationToken);
    }

}
