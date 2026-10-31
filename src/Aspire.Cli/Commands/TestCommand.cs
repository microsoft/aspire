// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Secrets;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Testing;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using StreamJsonRpc;

namespace Aspire.Cli.Commands;

internal sealed class TestCommand : BaseCommand
{
    internal static readonly TimeSpan s_gracefulShutdownBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_structuredLogSupportProbeTimeout = TimeSpan.FromSeconds(2);
    private const int MaxRememberedAppHostLogSequenceNumbers = 1000;
    private const int MaxDisplayedAppHostStartupOutputLines = 80;
    private static readonly Option<bool> s_noBuildOption = new("--no-build") { Description = TestCommandStrings.NoBuildArgumentDescription };
    private static readonly Option<bool> s_detachOption = new("--detach") { Hidden = true };
    internal static readonly Option<OutputFormat> s_formatOption = new("--format") { Description = TestCommandStrings.FormatDescription };
    internal static readonly Option<bool> s_keepAliveOption = new("--keep-alive") { Description = TestCommandStrings.KeepAliveDescription };
    private readonly IProjectLocator _projectLocator;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly AspireSecretsStoreResolver _secretsResolver;
    private readonly IConfiguration _configuration;
    private readonly FileLoggerProvider _fileLoggerProvider;
    private readonly ILogger<TestCommand> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CommonCommandServices _services;

    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;
    protected override TimeSpan GracefulShutdownBudget => s_gracefulShutdownBudget;

    public TestCommand(IProjectLocator projectLocator, IAppHostProjectFactory projectFactory,
        AspireSecretsStoreResolver secretsResolver, IConfiguration configuration,
        FileLoggerProvider fileLoggerProvider, CommonCommandServices services, TimeProvider timeProvider)
        : base("test", TestCommandStrings.Description, services)
    {
        _projectLocator = projectLocator;
        _projectFactory = projectFactory;
        _secretsResolver = secretsResolver;
        _configuration = configuration;
        _fileLoggerProvider = fileLoggerProvider;
        _logger = services.LoggerFactory.CreateLogger<TestCommand>();
        _services = services;
        _timeProvider = timeProvider;
        Options.Add(s_noBuildOption);
        Options.Add(s_detachOption);
        Options.Add(s_formatOption);
        Options.Add(s_keepAliveOption);
        Options.Add(AppHostLauncher.s_appHostOption);
        Options.Add(AppHostLauncher.s_isolatedOption);
        Options.Add(AppHostLauncher.s_launchProfileOption);
        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var format = parseResult.GetValue(s_formatOption);
        var extensionDebug = ExtensionHelper.IsExtensionHost(InteractionService, out _, out _) &&
            parseResult.GetValue(RootCommand.StartDebugSessionOption);
        if (parseResult.GetValue(s_detachOption) || parseResult.GetValue(RootCommand.CaptureProfileOption) ||
            extensionDebug || _configuration.GetBool(KnownConfigNames.CliRunDetached) is true)
        {
            var invalid = CommandResult.Failure(CliExitCodes.InvalidCommand, TestCommandStrings.IncompatibleOptions);
            WriteFailureJson(format, invalid);
            return invalid;
        }
        var timeoutSeconds = WaitCommand.DefaultTimeoutSeconds;
        var configuredTimeout = _configuration[CliConfigNames.AppHostStartupTimeout];
        if (!string.IsNullOrWhiteSpace(configuredTimeout) &&
            (!int.TryParse(configuredTimeout, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutSeconds) || timeoutSeconds <= 0))
        {
            var invalid = CommandResult.Failure(CliExitCodes.InvalidCommand,
                string.Format(CultureInfo.CurrentCulture, TestCommandStrings.InvalidStartupTimeout, CliConfigNames.AppHostStartupTimeout));
            WriteFailureJson(format, invalid);
            return invalid;
        }

        TestRunReporter? reporter = null;
        CommandResult result;
        try
        {
            reporter = TestRunReporter.Create(ExecutionContext, InteractionService,
                _services.HostEnvironment.SupportsInteractiveOutput, format, _timeProvider,
                _services.LoggerFactory.CreateLogger<TestRunReporter>());
            result = await RunTestAppHostAsync(parseResult, reporter, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = CommandResult.Cancelled();
        }
        catch (ProjectLocatorException ex)
        {
            result = HandleProjectLocatorException(ex, InteractionService, Telemetry);
        }
        catch (AppHostIncompatibleException ex)
        {
            result = CommandResult.FromExitCode(InteractionService.DisplayIncompatibleVersionError(ex,
                ex.AspireHostingVersion ?? ex.RequiredCapability));
        }
        catch (TestAppHostExitedException ex)
        {
            result = CommandResult.Failure(
                ex.ExitCode is CliExitCodes.Success or CliExitCodes.Cancelled ? CliExitCodes.TestRunFailed : ex.ExitCode,
                TestCommandStrings.AppHostExitedEarly);
        }
        catch (TestStartupTimeoutException)
        {
            result = CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost,
                string.Format(CultureInfo.CurrentCulture, TestCommandStrings.TimeoutWaitingForAppHost,
                    timeoutSeconds, CliConfigNames.AppHostStartupTimeout));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = CommandResult.Failure(CliExitCodes.TestRunFailed,
                string.Format(CultureInfo.CurrentCulture, TestCommandStrings.ArtifactFailure, ex.Message));
        }
        catch (Exception ex)
        {
            var error = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message);
            Telemetry.RecordError(error, ex);
            result = CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost, error);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            result = CommandResult.Cancelled();
        }
        try
        {
            if (reporter is not null)
            {
                reporter.Complete(result);
            }
            else
            {
                WriteFailureJson(format, result);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = string.Format(CultureInfo.CurrentCulture, TestCommandStrings.ArtifactFailure, ex.Message);
            if (result is { ExitCode: not CliExitCodes.Success })
            {
                InteractionService.DisplayError(error);
            }
            else
            {
                result = CommandResult.Failure(CliExitCodes.TestRunFailed, error);
            }
            if (format == OutputFormat.Json && reporter is not null)
            {
                reporter.WriteJsonResult(result);
            }
            else
            {
                WriteFailureJson(format, result);
            }
        }

        return result;
    }

    private async Task<CommandResult> RunTestAppHostAsync(ParseResult parseResult, TestRunReporter reporter,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        // Finite tests must not inherit run's watch, detached launch, or extension-managed
        // lifetime. Projects still own process creation; testing owns this orchestration.
        var behavior = _services.HostEnvironment.SupportsInteractiveInput
            ? MultipleAppHostProjectsFoundBehavior.Prompt : MultipleAppHostProjectsFoundBehavior.Throw;
        var search = await _projectLocator.UseOrFindAppHostProjectFileAsync(
            parseResult.GetValue(AppHostLauncher.s_appHostOption), behavior, createSettingsFile: true, cancellationToken).ConfigureAwait(false);
        if (search.SelectedProjectFile is not { } appHostFile)
        {
            return CommandResult.Failure(CliExitCodes.FailedToFindProject);
        }
        if (_projectFactory.TryGetProject(appHostFile) is not { } project)
        {
            return CommandResult.Failure(CliExitCodes.FailedToFindProject, TestCommandStrings.UnrecognizedAppHost);
        }
        var launchProfile = parseResult.GetValue(AppHostLauncher.s_launchProfileOption);
        if (AppHostLauncher.GetLaunchProfileValidationError(project, launchProfile) is { } profileError)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, profileError);
        }
        var isolated = AppHostLauncher.ResolveIsolated(parseResult);
        var runningInstance = await project.FindAndStopRunningInstanceAsync(
            appHostFile, ExecutionContext.HomeDirectory, cancellationToken).ConfigureAwait(false);
        if (isolated && runningInstance == RunningInstanceResult.InstanceStopped)
        {
            InteractionService.DisplayMessage(KnownEmojis.Warning, TestCommandStrings.IsolatedModeRunningInstanceWarning);
        }

        var built = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource<IAppHostCliBackchannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var noBuild = parseResult.GetValue(s_noBuildOption);
        var context = new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            IsTestRun = true,
            Watch = false,
            Debug = parseResult.GetValue(RootCommand.DebugOption),
            NoBuild = noBuild,
            NoRestore = noBuild,
            WaitForDebugger = parseResult.GetValue(RootCommand.WaitForDebuggerOption),
            Isolated = isolated,
            LaunchProfile = launchProfile,
            UnmatchedTokens = parseResult.UnmatchedTokens.ToArray(),
            WorkingDirectory = ExecutionContext.WorkingDirectory,
            BuildCompletionSource = built,
            BackchannelCompletionSource = connected,
        };
        context.EnvironmentVariables[KnownConfigNames.TestOutputPath] = reporter.OutputPath;
        context.EnvironmentVariables[KnownConfigNames.AspireUserSecretsId] =
            await _secretsResolver.ResolveAppHostIdAsync(appHostFile, project, cancellationToken).ConfigureAwait(false);
        ProfilingTelemetry.AddCurrentContextToEnvironment(context.EnvironmentVariables);

        using var runCancellation = new CancellationTokenSource();
        var forwarding = cancellationToken.Register(runCancellation.Cancel);
        using var logCancellation = new CancellationTokenSource();
        Task<int>? run = null;
        var logs = Task.CompletedTask;
        var startupOutputStartIndex = 0;
        var started = false;
        try
        {
            run = project.RunAsync(context, runCancellation.Token);
            // Restore/build must not consume the AppHost startup timeout. A project that
            // exits or faults without signalling build completion must not leave this wait pending.
            if (await Task.WhenAny(built.Task, run).WaitAsync(cancellationToken).ConfigureAwait(false) == run)
            {
                built.TrySetResult(false);
            }
            if (!await built.Task.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                var exitCode = await run.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (context.OutputCollector is { } output)
                {
                    InteractionService.DisplayLines(output.GetLines());
                }
                return CommandResult.Failure(exitCode == 0 ? CliExitCodes.FailedToDotnetRunAppHost : exitCode,
                    InteractionServiceStrings.ProjectCouldNotBeBuilt);
            }
            startupOutputStartIndex = context.OutputCollector?.GetLines().Count() ?? 0;
            if (context.WaitForDebugger)
            {
                InteractionService.DisplayMessage(KnownEmojis.Bug, InteractionServiceStrings.WaitingForDebuggerToAttachToAppHost);
            }

            using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var startupStart = _timeProvider.GetTimestamp();
            var startupTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            var backchannel = await InteractionService.ShowStatusAsync(TestCommandStrings.ConnectingToAppHost, async () =>
            {
                await WaitForStartupAsync(connected.Task, run, startupStart, startupTimeout, startupCancellation.Token).ConfigureAwait(false);
                return await connected.Task.ConfigureAwait(false);
            }).ConfigureAwait(false);
            logs = CaptureAppHostLogsAsync(_fileLoggerProvider, backchannel, InteractionService, logCancellation.Token);
            ObserveFaults(logs);
            DashboardUrlsState dashboard;
            try
            {
                dashboard = await InteractionService.ShowStatusAsync(TestCommandStrings.StartingDashboard, async () =>
                {
                    var urls = backchannel.GetDashboardUrlsAsync(startupCancellation.Token);
                    await WaitForStartupAsync(urls, run, startupStart, startupTimeout, startupCancellation.Token).ConfigureAwait(false);
                    return await urls.ConfigureAwait(false);
                }).ConfigureAwait(false);
                await WaitForStartupAsync(backchannel.NotifyAppHostReadyAsync(startupCancellation.Token),
                    run, startupStart, startupTimeout, startupCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                startupCancellation.Cancel();
            }
            started = true;
            if (dashboard.DashboardHealthy is false)
            {
                InteractionService.DisplayMessage(KnownEmojis.Warning, TestCommandStrings.DashboardFailedToStart);
            }
            return await ExecuteTestRunAsync(new TestAppHostSession(backchannel, run, dashboard,
                Path.GetRelativePath(ExecutionContext.WorkingDirectory.FullName, appHostFile.FullName),
                _fileLoggerProvider.LogFilePath, ExtensionHelper.IsExtensionHost(InteractionService, out _, out _)),
                reporter, parseResult.GetValue(s_keepAliveOption), parseResult.GetValue(s_formatOption), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!started)
            {
                var lines = context.OutputCollector?.GetLines().Skip(startupOutputStartIndex)
                    .Where(line => line.Stream == OutputLineStream.StdErr).TakeLast(MaxDisplayedAppHostStartupOutputLines).ToArray();
                if (lines is { Length: > 0 })
                {
                    InteractionService.DisplayMessage(KnownEmojis.Information, $"{TestCommandStrings.RecentAppHostStartupOutput}:");
                    InteractionService.DisplayLines(lines);
                }
            }
            throw;
        }
        finally
        {
            // Fence cancellation forwarding before the final unconditional teardown. Test
            // failures and RPC errors still own a live AppHost; none may leave it behind.
            await forwarding.DisposeAsync().ConfigureAwait(false);
            if (run is { IsCompleted: false })
            {
                runCancellation.Cancel();
                try
                {
                    var drainTimeout = _services.CancellationManager.IsCancellationRequested
                        ? Timeout.InfiniteTimeSpan : GracefulShutdownBudget;
                    await run.WaitAsync(drainTimeout, _timeProvider, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Test AppHost teardown did not finish normally.");
                    ObserveFaults(run);
                }
            }
            logCancellation.Cancel();
            try
            {
                await logs.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to capture logs from the test AppHost.");
                InteractionService.DisplayMessage(KnownEmojis.Warning, TestCommandStrings.LogCaptureFailed);
            }
        }
    }

    private async Task WaitForStartupAsync(Task readiness, Task<int> run, long startTimestamp,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var remaining = timeout - _timeProvider.GetElapsedTime(startTimestamp);
        var bounded = readiness.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _timeProvider, cancellationToken);
        try
        {
            if (await Task.WhenAny(bounded, run).WaitAsync(cancellationToken).ConfigureAwait(false) == run || run.IsCompleted)
            {
                throw new TestAppHostExitedException(await run.ConfigureAwait(false));
            }
            await bounded.ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TestStartupTimeoutException(ex);
        }
        finally
        {
            ObserveFaults(bounded);
            ObserveFaults(readiness);
        }
    }

    private void WriteFailureJson(OutputFormat format, CommandResult result)
    {
        if (format != OutputFormat.Json)
        {
            return;
        }
        var now = _timeProvider.GetUtcNow();
        var summary = new TestRunSummary
        {
            Status = result.ExitCode == CliExitCodes.Cancelled ? "cancelled" : "failed",
            StartedAt = now,
            CompletedAt = now,
            CliExitCode = result.ExitCode,
            Error = result.ErrorMessage
        };
        InteractionService.DisplayRawText(JsonSerializer.Serialize(summary, TestRunJsonContext.Default.TestRunSummary), ConsoleOutput.Standard);
    }

    private async Task<CommandResult> ExecuteTestRunAsync(TestAppHostSession session, TestRunReporter reporter,
        bool keepAlive, OutputFormat format, CancellationToken cancellationToken)
    {
        if (format != OutputFormat.Json)
        {
            RenderAppHostInformation(InteractionService, session.AppHostRelativePath,
                session.DashboardUrls.BaseUrlWithLoginToken, session.DashboardUrls.CodespacesUrlWithLoginToken,
                session.LogFilePath, session.IsExtensionHost);
        }
        if (ExtensionHelper.IsExtensionHost(InteractionService, out var extension, out _))
        {
            if (session.DashboardUrls.DashboardHealthy is true)
            {
                extension.DisplayDashboardUrls(session.DashboardUrls);
            }
            extension.NotifyAppHostStartupCompleted();
        }

        var capabilities = await session.Backchannel.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.Contains("test-reporting.v1", StringComparer.Ordinal))
        {
            return CommandResult.Failure(CliExitCodes.AppHostIncompatible, TestCommandStrings.AppHostIncompatible);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = reporter.WaitForCompletionAsync(session.Backchannel, stop.Token);
        try
        {
            var finished = await Task.WhenAny(completion, session.RunTask).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (finished == session.RunTask && !completion.IsCompleted)
            {
                return CommandResult.Failure(CliExitCodes.TestRunFailed, TestCommandStrings.AppHostExitedEarly);
            }
            var passed = await completion.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (keepAlive)
            {
                reporter.ReportCompletedResources(passed);
                InteractionService.DisplaySubtleMessage(TestCommandStrings.KeepAliveMessage);
                await WaitForInspectionAsync(session, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await session.Backchannel.RequestStopAsync(cancellationToken)
                        .WaitAsync(GracefulShutdownBudget, _timeProvider, cancellationToken).ConfigureAwait(false);
                    await session.RunTask.WaitAsync(GracefulShutdownBudget, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost,
                        string.Format(CultureInfo.CurrentCulture, TestCommandStrings.ShutdownTimeout, GracefulShutdownBudget.TotalSeconds));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var exitCode = await session.RunTask.ConfigureAwait(false);
            if (!passed)
            {
                return CommandResult.Failure(CliExitCodes.TestRunFailed);
            }
            // Guest launchers can report the signal used for orderly helper teardown.
            // Forced kill or another AppHost failure must not become a passing test run.
            return exitCode is 0 or 130 or 143 ? CommandResult.Success() :
                CommandResult.Failure(CliExitCodes.TestRunFailed,
                    string.Format(CultureInfo.CurrentCulture, TestCommandStrings.AppHostExitedWithCode, exitCode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Cancelled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandResult.Failure(CliExitCodes.TestRunFailed,
                string.Format(CultureInfo.CurrentCulture, TestCommandStrings.ArtifactFailure, ex.Message));
        }
        finally
        {
            stop.Cancel();
            ObserveFaults(completion);
        }
    }

    private async Task WaitForInspectionAsync(TestAppHostSession session, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var disconnected = session.Backchannel.WaitForDisconnectAsync(stop.Token);
        try
        {
            var finished = await Task.WhenAny(session.RunTask, disconnected).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (finished == disconnected)
            {
                await disconnected.ConfigureAwait(false);
                // A dashboard stop can close RPC before the process exits. Allow the normal
                // drain window, but do not leave a broken inspection session alive indefinitely.
                try
                {
                    await session.RunTask.WaitAsync(GracefulShutdownBudget, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new ConnectionLostException(TestCommandStrings.KeepAliveDisconnected);
                }
            }
            else
            {
                await session.RunTask.ConfigureAwait(false);
            }
        }
        finally
        {
            stop.Cancel();
            ObserveFaults(disconnected);
        }
    }

    private static void ObserveFaults(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal static void RenderAppHostInformation(IInteractionService console, string appHostRelativePath,
        string? dashboardUrl, string? codespacesUrl, string logFilePath, bool isExtensionHost)
    {
        console.DisplaySubtleMessage($"{TestCommandStrings.AppHost.EscapeMarkup()}: {appHostRelativePath.EscapeMarkup()}", allowMarkup: true);
        if (!isExtensionHost)
        {
            console.DisplaySubtleMessage($"{TestCommandStrings.Dashboard.EscapeMarkup()}: " +
                (string.IsNullOrEmpty(dashboardUrl) ? "N/A" : MarkupHelpers.SafeLink(console, dashboardUrl)), allowMarkup: true);
            if (!string.IsNullOrEmpty(codespacesUrl))
            {
                console.DisplaySubtleMessage($"{TestCommandStrings.Dashboard.EscapeMarkup()}: {MarkupHelpers.SafeLink(console, codespacesUrl)}", allowMarkup: true);
            }
        }
        console.DisplaySubtleMessage($"{TestCommandStrings.Logs.EscapeMarkup()}: {MarkupHelpers.SafeFileLink(console, logFilePath)}", allowMarkup: true);
    }
    private static async Task CaptureAppHostLogsAsync(FileLoggerProvider fileLoggerProvider, IAppHostCliBackchannel backchannel, IInteractionService interactionService, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();

            // Start the probe without awaiting it so extension responsiveness never delays
            // subscription to the AppHost stream or writes to the diagnostic log file.
            var structuredLogSupportProbe = ExtensionHelper.IsExtensionHost(interactionService, out var extensionInteractionService, out var extensionBackchannel)
                ? SupportsStructuredAppHostLogsAsync(fileLoggerProvider, extensionBackchannel, cancellationToken)
                : Task.FromResult(false);

            var logEntries = backchannel.GetAppHostLogEntriesAsync(cancellationToken);
            bool? extensionSupportsStructuredLogs = null;
            var pendingExtensionEntries = new List<BackchannelLogEntry>();
            var recentSequenceIdentities = new HashSet<(Guid GenerationId, long SequenceNumber)>(MaxRememberedAppHostLogSequenceNumbers);
            var sequenceIdentityOrder = new Queue<(Guid GenerationId, long SequenceNumber)>(MaxRememberedAppHostLogSequenceNumbers);

            try
            {
                using var enumerationCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await using var enumerator = logEntries.GetAsyncEnumerator(enumerationCancellationSource.Token);
                Task<bool>? moveNextTask = null;

                try
                {
                    while (true)
                    {
                        moveNextTask ??= enumerator.MoveNextAsync().AsTask();

                        // Once an entry is buffered, keep one MoveNextAsync in flight and observe the
                        // capability probe alongside it. This preserves file capture throughput while
                        // allowing an idle stream to flush as soon as the extension answers.
                        if (extensionSupportsStructuredLogs is null && pendingExtensionEntries.Count > 0)
                        {
                            var probeCompletedFirst = structuredLogSupportProbe.IsCompleted ||
                                await Task.WhenAny(moveNextTask, structuredLogSupportProbe).ConfigureAwait(false) == structuredLogSupportProbe;
                            if (probeCompletedFirst)
                            {
                                extensionSupportsStructuredLogs = await structuredLogSupportProbe.ConfigureAwait(false);
                                ForwardPendingAppHostLogEntriesToExtension(
                                    extensionInteractionService!,
                                    extensionSupportsStructuredLogs.Value,
                                    pendingExtensionEntries);
                                continue;
                            }
                        }

                        var hasEntry = await moveNextTask.ConfigureAwait(false);
                        moveNextTask = null;
                        if (!hasEntry)
                        {
                            break;
                        }

                        var entry = enumerator.Current;

                        // A reconnect replays the AppHost's 1,000-entry buffer. Remember exact
                        // generation/sequence pairs to preserve delayed delivery and distinguish a
                        // replacement AppHost. Sequence zero comes from older AppHosts and has no
                        // stable identity.
                        if (entry.SequenceNumber > 0)
                        {
                            var sequenceIdentity = (entry.GenerationId, entry.SequenceNumber);
                            if (!recentSequenceIdentities.Add(sequenceIdentity))
                            {
                                continue;
                            }

                            sequenceIdentityOrder.Enqueue(sequenceIdentity);
                            if (sequenceIdentityOrder.Count > MaxRememberedAppHostLogSequenceNumbers)
                            {
                                recentSequenceIdentities.Remove(sequenceIdentityOrder.Dequeue());
                            }
                        }

                        var shortCategory = FileLoggerProvider.GetShortCategoryName(entry.CategoryName);
                        var message = string.IsNullOrEmpty(entry.Exception)
                            ? entry.Message
                            : $"{entry.Message}{Environment.NewLine}{entry.Exception}";
                        fileLoggerProvider.WriteLog(entry.Timestamp, entry.LogLevel, $"AppHost/{shortCategory}", message);

                        // Preserve the previous RPC volume. Trace and Debug still arrive through the
                        // AppHost console provider and are styled by the extension.
                        if (extensionInteractionService is null || entry.LogLevel is LogLevel.Trace or LogLevel.Debug)
                        {
                            continue;
                        }

                        if (extensionSupportsStructuredLogs is null)
                        {
                            pendingExtensionEntries.Add(entry);
                            continue;
                        }

                        ForwardAppHostLogEntryToExtension(extensionInteractionService, extensionSupportsStructuredLogs.Value, entry);
                    }
                }
                finally
                {
                    if (moveNextTask is not null)
                    {
                        // Forwarding can fail while the stream read is still pending. Cancel and
                        // settle that read before DisposeAsync touches the enumerator.
                        await enumerationCancellationSource.CancelAsync().ConfigureAwait(false);
                        try
                        {
                            await moveNextTask.ConfigureAwait(false);
                        }
                        catch (Exception) when (enumerationCancellationSource.IsCancellationRequested)
                        {
                        }
                    }
                }
            }
            catch (Exception ex) when (BackchannelDisconnectHelpers.IsExpectedDisconnect(ex))
            {
                // The AppHost process exited and the backchannel connection was lost. This is
                // expected during orderly shutdown, but buffered records still need to be flushed.
            }

            if (!cancellationToken.IsCancellationRequested && extensionInteractionService is not null && pendingExtensionEntries.Count > 0)
            {
                extensionSupportsStructuredLogs ??= await structuredLogSupportProbe.ConfigureAwait(false);
                ForwardPendingAppHostLogEntriesToExtension(
                    extensionInteractionService,
                    extensionSupportsStructuredLogs.Value,
                    pendingExtensionEntries);
            }
        }
        catch (OperationCanceledException)
        {
            // Swallow the exception if the operation was cancelled.
            return;
        }
        catch (Exception ex) when (BackchannelDisconnectHelpers.IsExpectedDisconnect(ex))
        {
            // The AppHost process exited and the backchannel connection was lost. This is
            // expected during orderly shutdown — the connection drops before the cancellation
            // token fires because logCaptureCancellationSource.Cancel() runs in the finally
            // block after the AppHost process has already exited.
            return;
        }
    }

    private static void ForwardPendingAppHostLogEntriesToExtension(
        IExtensionInteractionService extensionInteractionService,
        bool extensionSupportsStructuredLogs,
        List<BackchannelLogEntry> pendingExtensionEntries)
    {
        foreach (var pendingEntry in pendingExtensionEntries)
        {
            ForwardAppHostLogEntryToExtension(extensionInteractionService, extensionSupportsStructuredLogs, pendingEntry);
        }

        pendingExtensionEntries.Clear();
    }

    private static void ForwardAppHostLogEntryToExtension(
        IExtensionInteractionService extensionInteractionService,
        bool extensionSupportsStructuredLogs,
        BackchannelLogEntry entry)
    {
        // Older AppHosts deserialize the added sequence as 0. Only numbered records have
        // the identity needed to suppress reconnect replays safely.
        if (extensionSupportsStructuredLogs && entry.SequenceNumber > 0)
        {
            extensionInteractionService.WriteAppHostLogEntry(new ExtensionAppHostLogEntry
            {
                GenerationId = entry.GenerationId,
                SequenceNumber = entry.SequenceNumber,
                LogLevel = entry.LogLevel.ToString(),
                Message = entry.Message,
                CategoryName = entry.CategoryName,
                EventId = entry.EventId.Id,
                Exception = entry.Exception,
            });
        }
        else
        {
            // Older extensions only accept plain debug-session messages.
            extensionInteractionService.WriteDebugSessionMessage(entry.Message, entry.LogLevel is not LogLevel.Error and not LogLevel.Critical, "\x1b[2m");
        }
    }

    private static async Task<bool> SupportsStructuredAppHostLogsAsync(
        FileLoggerProvider fileLoggerProvider,
        IExtensionBackchannel extensionBackchannel,
        CancellationToken cancellationToken)
    {
        try
        {
            return await extensionBackchannel.HasCapabilityAsync(KnownCapabilities.AppHostLogOutput, cancellationToken)
                .WaitAsync(s_structuredLogSupportProbeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            fileLoggerProvider.WriteLog(
                DateTimeOffset.UtcNow,
                LogLevel.Debug,
                "Aspire.Cli",
                "Structured AppHost log capability probe failed; using legacy debug console output.",
                ex);
            return false;
        }

    }

    private sealed record TestAppHostSession(
        IAppHostCliBackchannel Backchannel, Task<int> RunTask, DashboardUrlsState DashboardUrls,
        string AppHostRelativePath, string LogFilePath, bool IsExtensionHost);

    private sealed class TestAppHostExitedException(int exitCode) : Exception(TestCommandStrings.AppHostExitedEarly)
    {
        public int ExitCode { get; } = exitCode;
    }

    private sealed class TestStartupTimeoutException(TimeoutException innerException) : Exception(innerException.Message, innerException);
}
