// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Exceptions;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Spectre.Console;
using StreamJsonRpc;

namespace Aspire.Cli.Commands;

internal abstract class PipelineCommandBase : BaseCommand
{
    private const string CustomChoiceValue = "__CUSTOM_CHOICE";

    protected readonly IDotNetCliRunner _runner;
    protected readonly IProjectLocator _projectLocator;
    protected readonly IAppHostProjectFactory _projectFactory;

    private readonly IConfiguration _configuration;
    private readonly IFeatures _features;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly ILogger _logger;
    private readonly IAnsiConsole _ansiConsole;

    protected static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", PublishCommandStrings.ProjectArgumentDescription);

    private readonly Option<string?> _outputPathOption;
    private readonly Option<bool>? _startDebugSessionOption;

    protected static readonly Option<string?> s_logLevelOption = new("--log-level")
    {
        Description = SharedCommandStrings.PipelineLogLevelOptionDescription
    };

    protected static readonly Option<bool> s_includeExceptionDetailsOption = new("--include-exception-details")
    {
        Description = SharedCommandStrings.PipelineIncludeExceptionDetailsOptionDescription
    };

    protected static readonly Option<string?> s_environmentOption = new("--environment", "-e")
    {
        Description = SharedCommandStrings.PipelineEnvironmentOptionDescription
    };

    protected static readonly Option<bool> s_noBuildOption = new("--no-build")
    {
        Description = PublishCommandStrings.NoBuildArgumentDescription
    };

    protected abstract string OperationCompletedPrefix { get; }
    protected abstract string OperationFailedPrefix { get; }

    private static bool IsCompletionStateComplete(string completionState) =>
        completionState is CompletionStates.Completed or CompletionStates.CompletedWithWarning or CompletionStates.CompletedWithError;

    private static bool IsCompletionStateError(string completionState) =>
        completionState == CompletionStates.CompletedWithError;

    private static bool IsCompletionStateWarning(string completionState) =>
        completionState == CompletionStates.CompletedWithWarning;

    protected PipelineCommandBase(string name, string description, IDotNetCliRunner runner, IInteractionService interactionService, IProjectLocator projectLocator, AspireCliTelemetry telemetry, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory, IConfiguration configuration, ILogger logger, IAnsiConsole ansiConsole)
        : base(name, description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _runner = runner;
        _projectLocator = projectLocator;
        _hostEnvironment = hostEnvironment;
        _configuration = configuration;
        _features = features;
        _projectFactory = projectFactory;
        _logger = logger;
        _ansiConsole = ansiConsole;

        _outputPathOption = new Option<string?>("--output-path", "-o")
        {
            Description = GetOutputPathDescription()
        };

        Options.Add(s_appHostOption);
        Options.Add(_outputPathOption);
        Options.Add(s_logLevelOption);
        Options.Add(s_environmentOption);
        Options.Add(s_includeExceptionDetailsOption);
        Options.Add(s_noBuildOption);

        if (ExtensionHelper.IsExtensionHost(interactionService, out _, out _))
        {
            _startDebugSessionOption = new Option<bool>("--start-debug-session")
            {
                Description = RunCommandStrings.StartDebugSessionArgumentDescription
            };
            Options.Add(_startDebugSessionOption);
        }

        // In the publish and deploy commands we forward all unrecognized tokens
        // through to the underlying tooling when we launch the app host.
        TreatUnmatchedTokensAsErrors = false;
    }

    protected abstract string GetOutputPathDescription();
    protected abstract Task<string[]> GetRunArgumentsAsync(string? fullyQualifiedOutputPath, string[] unmatchedTokens, ParseResult parseResult, CancellationToken cancellationToken);
    protected abstract string GetCanceledMessage();
    protected abstract string GetProgressMessage(ParseResult parseResult);

    /// <summary>
    /// Gets command-specific arguments to forward when starting a debug session from the extension context.
    /// Subclasses should override to include their specific positional arguments.
    /// Unmatched tokens are always included automatically.
    /// </summary>
    protected virtual string[] GetCommandArgs(ParseResult parseResult) => [];

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // If running in the extension context (Aspire terminal) without a debug session,
        // intercept and tell VS Code to start a proper debug session for this command.
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        if (ExtensionHelper.IsExtensionHost(InteractionService, out var extensionInteractionService, out _)
            && string.IsNullOrEmpty(_configuration[KnownConfigNames.ExtensionDebugSessionId]))
        {
            // Resolve the apphost project interactively before starting the debug session,
            // so the user is prompted if needed and we can pass it along.
            if (passedAppHostProjectFile is null)
            {
                var searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, MultipleAppHostProjectsFoundBehavior.Prompt, createSettingsFile: true, cancellationToken);
                passedAppHostProjectFile = searchResult.SelectedProjectFile;

                if (passedAppHostProjectFile is null)
                {
                    return ExitCodeConstants.FailedToFindProject;
                }
            }

            var commandArgs = GetCommandArgs(parseResult).Concat(parseResult.UnmatchedTokens).ToArray();

            extensionInteractionService.DisplayConsolePlainText($"Detected aspire {Name} inside the Aspire extension, starting a debug session in VS Code...");
            await extensionInteractionService.StartDebugSessionAsync(ExecutionContext.WorkingDirectory.FullName, passedAppHostProjectFile?.FullName, debug: true, new DebugSessionOptions { Command = Name, Args = commandArgs.Length > 0 ? commandArgs : null });
            return ExitCodeConstants.Success;
        }

        var debugMode = parseResult.GetValue(RootCommand.DebugOption);
        var waitForDebugger = parseResult.GetValue(RootCommand.WaitForDebuggerOption);
        var noBuild = parseResult.GetValue(s_noBuildOption);
        var startDebugSession = _startDebugSessionOption is not null && parseResult.GetValue(_startDebugSessionOption);

        Task<int>? pendingRun = null;
        PublishContext? publishContext = null;

        // Send terminal infinite progress bar start sequence
        StartTerminalProgressBar();

        try
        {
            using var activity = Telemetry.StartDiagnosticActivity(this.Name);

            var searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, MultipleAppHostProjectsFoundBehavior.Prompt, createSettingsFile: true, cancellationToken);
            var effectiveAppHostFile = searchResult.SelectedProjectFile;

            if (effectiveAppHostFile is null)
            {
                // Send terminal progress bar stop sequence
                StopTerminalProgressBar();
                return ExitCodeConstants.FailedToFindProject;
            }

            var project = _projectFactory.GetProject(effectiveAppHostFile);

            var env = new Dictionary<string, string>();

            // Set interactivity enabled based on host environment capabilities
            if (!_hostEnvironment.SupportsInteractiveInput)
            {
                env[KnownConfigNames.InteractivityEnabled] = "false";
            }

            if (waitForDebugger)
            {
                env[KnownConfigNames.WaitForDebugger] = "true";
            }

            var outputPath = parseResult.GetValue(_outputPathOption);
            var fullyQualifiedOutputPath = outputPath != null ? Path.GetFullPath(outputPath) : null;

            var backchannelCompletionSource = new TaskCompletionSource<IAppHostCliBackchannel>();

            var unmatchedTokens = parseResult.UnmatchedTokens.ToArray();

            // Create the publish context and delegate to IAppHostProject
            publishContext = new PublishContext
            {
                AppHostFile = effectiveAppHostFile,
                OutputPath = fullyQualifiedOutputPath,
                EnvironmentVariables = env,
                Arguments = await GetRunArgumentsAsync(fullyQualifiedOutputPath, unmatchedTokens, parseResult, cancellationToken),
                BackchannelCompletionSource = backchannelCompletionSource,
                WorkingDirectory = ExecutionContext.WorkingDirectory,
                Debug = debugMode,
                StartDebugSession = startDebugSession,
                NoBuild = noBuild
            };

            pendingRun = project.PublishAsync(publishContext, cancellationToken);

            // If we use the --wait-for-debugger option we print out the process ID
            // of the apphost so that the user can attach to it.
            if (waitForDebugger)
            {
                InteractionService.DisplayMessage(KnownEmojis.Bug, InteractionServiceStrings.WaitingForDebuggerToAttachToAppHost);
            }

            var backchannel = await InteractionService.ShowStatusAsync(GetProgressMessage(parseResult), async () =>
            {
                var completedTask = await Task.WhenAny(backchannelCompletionSource.Task, pendingRun);
                if (completedTask == backchannelCompletionSource.Task)
                {
                    return await backchannelCompletionSource.Task;
                }

                // Check if the task faulted with a known exception type that should be propagated directly
                if (completedTask.IsFaulted && completedTask.Exception?.InnerException is DotNetSdkNotInstalledException sdkException)
                {
                    throw sdkException;
                }

                // When running in extension context, the extension takes over apphost management.
                // DotNetCliRunner returns Success immediately after delegating to LaunchAppHostAsync,
                // so pendingRun completes before the backchannel is established. In this case,
                // continue waiting for the backchannel rather than throwing.
                if (!completedTask.IsFaulted && await pendingRun == ExitCodeConstants.Success
                    && ExtensionHelper.IsExtensionHost(InteractionService, out _, out _))
                {
                    return await backchannelCompletionSource.Task;
                }

                // Throw an error if the run completed without returning a backchannel.
                // Include possible error if the run task faulted.
                var innerException = completedTask.IsFaulted ? completedTask.Exception : null;
                throw new InvalidOperationException("Run completed without returning a backchannel.", innerException);
            }, emoji: KnownEmojis.HammerAndWrench);

            var publishingActivities = backchannel.GetPublishingActivitiesAsync(cancellationToken);

            // Check if debug or trace logging is enabled
            var logLevel = parseResult.GetValue(s_logLevelOption);
            var isDebugOrTraceLoggingEnabled = logLevel?.Equals("debug", StringComparison.OrdinalIgnoreCase) == true ||
                                                 logLevel?.Equals("trace", StringComparison.OrdinalIgnoreCase) == true;

            var noFailuresReported = debugMode switch
            {
                true => await ProcessPublishingActivitiesDebugAsync(publishingActivities, backchannel, cancellationToken),
                false => await ProcessAndDisplayPublishingActivitiesAsync(publishingActivities, backchannel, isDebugOrTraceLoggingEnabled, cancellationToken),
            };

            // Send terminal progress bar stop sequence
            StopTerminalProgressBar();

            await backchannel.RequestStopAsync(cancellationToken).ConfigureAwait(false);
            var exitCode = await pendingRun;

            // If the apphost returned a non-zero exit code, use it directly.
            // This ensures we properly propagate apphost failures (e.g., exceptions, crashes).
            if (exitCode != 0)
            {
                if (debugMode && publishContext?.OutputCollector is { } outputCollector)
                {
                    InteractionService.DisplayLines(outputCollector.GetLines());
                }
                return exitCode;
            }

            // If the apphost exited successfully (0) but reported failures via backchannel,
            // return a failure exit code.
            if (!noFailuresReported)
            {
                return ExitCodeConstants.FailedToBuildArtifacts;
            }

            // Both apphost exit code and backchannel indicate success
            return ExitCodeConstants.Success;
        }
        catch (OperationCanceledException ex)
        {
            // Send terminal progress bar stop sequence on cancellation
            StopTerminalProgressBar();
            _logger.LogDebug(ex, "Operation was cancelled.");
            var canceledMessage = GetCanceledMessage();
            Telemetry.RecordError(canceledMessage, ex);
            InteractionService.DisplayError(canceledMessage);
            return ExitCodeConstants.FailedToBuildArtifacts;
        }
        catch (ProjectLocatorException ex)
        {
            // Send terminal progress bar stop sequence on exception
            StopTerminalProgressBar();
            return HandleProjectLocatorException(ex, InteractionService, Telemetry);
        }
        catch (DotNetSdkNotInstalledException)
        {
            // SDK not installed - message already displayed by EnsureSdkInstalledAsync
            StopTerminalProgressBar();
            return ExitCodeConstants.SdkNotInstalled;
        }
        catch (AppHostIncompatibleException ex)
        {
            // Send terminal progress bar stop sequence on exception
            StopTerminalProgressBar();
            Telemetry.RecordError($"AppHost is incompatible. Required capability: {ex.RequiredCapability}", ex);
            InteractionService.DisplayError(ex.Message);
            return ExitCodeConstants.AppHostIncompatible;
        }
        catch (FailedToConnectBackchannelConnection ex)
        {
            // Send terminal progress bar stop sequence on exception
            StopTerminalProgressBar();
            Telemetry.RecordError("Failed to connect to AppHost backchannel.", ex);
            var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ErrorConnectingToAppHost, ex.Message);
            InteractionService.DisplayError(errorMessage);
            if (publishContext?.OutputCollector is { } outputCollector)
            {
                InteractionService.DisplayLines(outputCollector.GetLines());
            }
            return ExitCodeConstants.FailedToBuildArtifacts;
        }
        catch (ConnectionLostException ex)
        {
            // Occurs if the apphost RPC channel is lost unexpectedly.
            StopTerminalProgressBar();
            Telemetry.RecordError("Connection to AppHost was lost unexpectedly.", ex);
            var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.AppHostConnectionLost, ex.Message);
            InteractionService.DisplayError(errorMessage);
            if (publishContext?.OutputCollector is { } outputCollector)
            {
                InteractionService.DisplayLines(outputCollector.GetLines());
            }
            return pendingRun is { } && debugMode ? await pendingRun : ExitCodeConstants.FailedToBuildArtifacts;
        }
        catch (Exception ex)
        {
            // Send terminal progress bar stop sequence on exception
            StopTerminalProgressBar();
            Telemetry.RecordError("An unexpected error occurred during pipeline execution.", ex);
            var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message);
            InteractionService.DisplayError(errorMessage);
            if (publishContext?.OutputCollector is { } outputCollector)
            {
                InteractionService.DisplayLines(outputCollector.GetLines());
            }
            return ExitCodeConstants.FailedToBuildArtifacts;
        }
    }

    /// <summary>
    /// Conditionally converts markdown to Spectre markup based on the EnableMarkdown flag in the activity data.
    /// </summary>
    /// <param name="text">The text to convert.</param>
    /// <param name="activityData">The publishing activity data containing the EnableMarkdown flag.</param>
    /// <returns>The converted text if markdown is enabled, otherwise the original text.</returns>
    private static string ConvertTextWithMarkdownFlag(string text, PublishingActivityData activityData)
    {
        return activityData.EnableMarkdown ? MarkdownToSpectreConverter.ConvertToSpectre(text) : text.EscapeMarkup();
    }

    public async Task<bool> ProcessPublishingActivitiesDebugAsync(IAsyncEnumerable<PublishingActivity> publishingActivities, IAppHostCliBackchannel backchannel, CancellationToken cancellationToken)
    {
        var stepCounter = 1;
        var steps = new Dictionary<string, string>();
        PublishingActivity? publishingActivity = null;

        await foreach (var activity in publishingActivities.WithCancellation(cancellationToken))
        {
            StartTerminalProgressBar();
            if (activity.Type == PublishingActivityTypes.PublishComplete)
            {
                publishingActivity = activity;
                break;
            }
            else if (activity.Type == PublishingActivityTypes.Step)
            {
                if (!steps.TryGetValue(activity.Data.Id, out var stepStatus))
                {
                    // New step - log it
                    var statusText = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);
                    InteractionService.DisplaySubtleMessage($"[[DEBUG]] Step {stepCounter++}: {statusText}", allowMarkup: true);
                    steps[activity.Data.Id] = activity.Data.CompletionState;
                }
                else if (IsCompletionStateComplete(activity.Data.CompletionState))
                {
                    // Step completed - log completion
                    var status = IsCompletionStateError(activity.Data.CompletionState) ? "FAILED" :
                        IsCompletionStateWarning(activity.Data.CompletionState) ? "WARNING" : "COMPLETED";
                    var statusText = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);
                    InteractionService.DisplaySubtleMessage($"[[DEBUG]] Step {activity.Data.Id}: {status} - {statusText}", allowMarkup: true);
                    steps[activity.Data.Id] = activity.Data.CompletionState;
                }
            }
            else if (activity.Type == PublishingActivityTypes.Prompt)
            {
                await HandlePromptActivityAsync(activity, backchannel, cancellationToken);
            }
            else if (activity.Type == PublishingActivityTypes.Log)
            {
                // Log activity - display the log message
                var logLevel = activity.Data.LogLevel ?? "Information";
                var message = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);
                var timestamp = activity.Data.Timestamp?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? DateTimeOffset.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

                // Use 3-letter prefixes for log levels
                var logPrefix = logLevel.ToUpperInvariant() switch
                {
                    "DEBUG" => "DBG",
                    "TRACE" => "TRC",
                    "INFORMATION" => "INF",
                    "WARNING" => "WRN",
                    "ERROR" => "ERR",
                    "CRITICAL" => "CRT",
                    _ => "INF"
                };

                // Make debug and trace logs more subtle
                var formattedMessage = logLevel.ToUpperInvariant() switch
                {
                    "DEBUG" => $"[[{timestamp}]] [dim][[{logPrefix}]] {message}[/]",
                    "TRACE" => $"[[{timestamp}]] [dim][[{logPrefix}]] {message}[/]",
                    _ => $"[[{timestamp}]] [[{logPrefix}]] {message}"
                };

                InteractionService.DisplaySubtleMessage(formattedMessage, allowMarkup: true);
            }
            else
            {
                // Task activity - log it
                var stepId = activity.Data.StepId;
                if (IsCompletionStateComplete(activity.Data.CompletionState))
                {
                    var status = IsCompletionStateError(activity.Data.CompletionState) ? "FAILED" :
                        IsCompletionStateWarning(activity.Data.CompletionState) ? "WARNING" : "COMPLETED";
                    var statusText = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);
                    InteractionService.DisplaySubtleMessage($"[[DEBUG]] Task {activity.Data.Id} ({stepId}): {status} - {statusText}", allowMarkup: true);
                    if (!string.IsNullOrEmpty(activity.Data.CompletionMessage))
                    {
                        var completionMessage = ConvertTextWithMarkdownFlag(activity.Data.CompletionMessage, activity.Data);
                        InteractionService.DisplaySubtleMessage($"[[DEBUG]]   {completionMessage}", allowMarkup: true);
                    }
                }
                else
                {
                    var statusText = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);
                    InteractionService.DisplaySubtleMessage($"[[DEBUG]] Task {activity.Data.Id} ({stepId}): {statusText}", allowMarkup: true);
                }
            }
        }

        var hasErrors = publishingActivity is not null && IsCompletionStateError(publishingActivity.Data.CompletionState);
        var hasWarnings = publishingActivity is not null && IsCompletionStateWarning(publishingActivity.Data.CompletionState);

        if (publishingActivity is not null)
        {
            var status = hasErrors ? "FAILED" : hasWarnings ? "WARNING" : "COMPLETED";
            var statusText = ConvertTextWithMarkdownFlag(publishingActivity.Data.StatusText, publishingActivity.Data);
            InteractionService.DisplaySubtleMessage($"[[DEBUG]] {OperationCompletedPrefix}: {status} - {statusText}", allowMarkup: true);

            // Send visual bell notification when operation is complete
            Console.Write("\a");
            Console.Out.Flush();
        }

        return !hasErrors;
    }

    public async Task<bool> ProcessAndDisplayPublishingActivitiesAsync(IAsyncEnumerable<PublishingActivity> publishingActivities, IAppHostCliBackchannel backchannel, bool isDebugOrTraceLoggingEnabled, CancellationToken cancellationToken)
    {
        var stepCounter = 1;
        var steps = new Dictionary<string, StepInfo>();
        var logger = new ConsoleActivityLogger(_ansiConsole, _hostEnvironment, isDebugOrTraceLoggingEnabled);
        logger.StartSpinner();
        PublishingActivity? publishingActivity = null;

        try
        {
            await foreach (var activity in publishingActivities.WithCancellation(cancellationToken))
            {
                StartTerminalProgressBar();
                if (activity.Type == PublishingActivityTypes.PublishComplete)
                {
                    publishingActivity = activity;
                    break;
                }
                else if (activity.Type == PublishingActivityTypes.Step)
                {
                    if (!steps.TryGetValue(activity.Data.Id, out var stepInfo))
                    {
                        var title = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);
                        stepInfo = new StepInfo
                        {
                            Id = activity.Data.Id,
                            Title = title,
                            Number = stepCounter++,
                            StartTime = DateTime.UtcNow,
                            CompletionState = activity.Data.CompletionState,
                            ParentStepId = activity.Data.ParentStepId,
                            HierarchyLevel = activity.Data.HierarchyLevel ?? 0
                        };

                        steps[activity.Data.Id] = stepInfo;
                        // Use the stable step Id for logger state tracking (prevents duplicate counting when titles repeat)
                        logger.StartTask(stepInfo.Id, stepInfo.Title, $"Starting {stepInfo.Title}...");
                    }
                    else if (IsCompletionStateComplete(activity.Data.CompletionState))
                    {
                        stepInfo.ParentStepId ??= activity.Data.ParentStepId;
                        stepInfo.HierarchyLevel = activity.Data.HierarchyLevel ?? stepInfo.HierarchyLevel;
                        stepInfo.CompletionState = activity.Data.CompletionState;
                        stepInfo.CompletionText = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);
                        stepInfo.EndTime = DateTime.UtcNow;
                        if (IsCompletionStateError(stepInfo.CompletionState))
                        {
                            logger.Failure(stepInfo.Id, stepInfo.CompletionText);
                        }
                        else if (IsCompletionStateWarning(stepInfo.CompletionState))
                        {
                            logger.Warning(stepInfo.Id, stepInfo.CompletionText);
                        }
                        else
                        {
                            logger.Success(stepInfo.Id, stepInfo.CompletionText);
                        }
                    }
                }
                else if (activity.Type == PublishingActivityTypes.Prompt)
                {
                    await logger.StopSpinnerAsync();
                    await HandlePromptActivityAsync(activity, backchannel, cancellationToken);
                    logger.StartSpinner();
                }
                else if (activity.Type == PublishingActivityTypes.Log)
                {
                    // Log activity - display through logger based on log level
                    var stepId = activity.Data.StepId;
                    if (stepId != null && steps.TryGetValue(stepId, out var stepInfo))
                    {
                        var logLevel = activity.Data.LogLevel ?? "Information";
                        var message = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);

                        // Add 3-letter prefix to message for consistency
                        var logPrefix = logLevel.ToUpperInvariant() switch
                        {
                            "DEBUG" => "DBG",
                            "TRACE" => "TRC",
                            "INFORMATION" => "INF",
                            "WARNING" => "WRN",
                            "ERROR" => "ERR",
                            "CRITICAL" => "CRT",
                            _ => "INF"
                        };

                        var prefixedMessage = $"[[{logPrefix}]] {message}";

                        // Map log levels to appropriate console logger methods
                        switch (logLevel.ToUpperInvariant())
                        {
                            case "ERROR":
                            case "CRITICAL":
                                logger.Failure(stepInfo.Id, prefixedMessage);
                                break;
                            case "WARNING":
                            case "WARN":
                                logger.Warning(stepInfo.Id, prefixedMessage);
                                break;
                            case "DEBUG":
                            case "TRACE":
                                // Use a more subtle approach for debug/trace - prefix with dim formatting
                                var subtleMessage = $"[dim]{prefixedMessage}[/]";
                                logger.Info(stepInfo.Id, subtleMessage);
                                break;
                            case "INFORMATION":
                            case "INFO":
                            default:
                                logger.Info(stepInfo.Id, prefixedMessage);
                                break;
                        }
                    }
                }
                else
                {
                    var stepId = activity.Data.StepId;
                    Debug.Assert(stepId != null, "Activity data should have a StepId for task activities.");

                    if (!steps.TryGetValue(stepId, out var stepInfo))
                    {
                        throw new InvalidOperationException($"Step '{stepId}' not found for task '{activity.Data.Id}'");
                    }

                    var tasks = stepInfo.Tasks;

                    if (!tasks.TryGetValue(activity.Data.Id, out var task))
                    {
                        var statusText = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);
                        task = new TaskInfo
                        {
                            Id = activity.Data.Id,
                            StatusText = statusText,
                            StartTime = DateTime.UtcNow,
                            CompletionState = activity.Data.CompletionState
                        };

                        tasks[activity.Data.Id] = task;
                        logger.Progress(stepInfo.Id, statusText);
                    }

                    task.StatusText = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);
                    task.CompletionState = activity.Data.CompletionState;

                    if (IsCompletionStateComplete(activity.Data.CompletionState))
                    {
                        task.CompletionMessage = !string.IsNullOrEmpty(activity.Data.CompletionMessage)
                            ? ConvertTextWithMarkdownFlag(activity.Data.CompletionMessage, activity.Data)
                            : null;

                        var duration = DateTime.UtcNow - task.StartTime;
                        var durationStr = $"({duration.TotalSeconds:F1}s)";

                        var message = !string.IsNullOrEmpty(task.CompletionMessage)
                            ? $"{task.CompletionMessage} {durationStr}"
                            : $"{task.StatusText} {durationStr}";

                        if (IsCompletionStateError(task.CompletionState))
                        {
                            logger.Failure(stepInfo.Id, message);
                        }
                        else if (IsCompletionStateWarning(task.CompletionState))
                        {
                            logger.Warning(stepInfo.Id, message);
                        }
                        else
                        {
                            logger.Success(stepInfo.Id, message);
                        }

                        // If this task caused the step to fail, record a candidate failure reason if not already set.
                        if (IsCompletionStateError(task.CompletionState) && string.IsNullOrEmpty(stepInfo.FailureReason))
                        {
                            stepInfo.FailureReason = task.CompletionMessage ?? task.StatusText;
                        }
                    }
                }
            }

            if (publishingActivity is not null)
            {
                var hasErrors = IsCompletionStateError(publishingActivity.Data.CompletionState);
                var hasWarnings = IsCompletionStateWarning(publishingActivity.Data.CompletionState);
                // Determine first failed step (if any) for failure detail.
                string? failedStepTitle = null;
                string? failedStepMessage = null;
                if (hasErrors)
                {
                    var failedStep = steps.Values.FirstOrDefault(s => IsCompletionStateError(s.CompletionState));
                    if (failedStep is not null)
                    {
                        failedStepTitle = failedStep.Title;
                        failedStepMessage = failedStep.FailureReason ?? failedStep.CompletionText;
                    }
                }

                // Build duration breakdown, ordered by step sequence
                var now = DateTime.UtcNow;
                var earliestStartTime = steps.Count > 0
                    ? steps.Values.Min(s => s.StartTime)
                    : now;
                var durationRecords = steps.Values.Select(s =>
                {
                    var end = s.EndTime ?? now;
                    var state = s.CompletionState switch
                    {
                        var cs when IsCompletionStateError(cs) => ConsoleActivityLogger.ActivityState.Failure,
                        var cs when IsCompletionStateWarning(cs) => ConsoleActivityLogger.ActivityState.Warning,
                        var cs when cs == CompletionStates.Completed => ConsoleActivityLogger.ActivityState.Success,
                        _ => ConsoleActivityLogger.ActivityState.InProgress
                    };
                    return new ConsoleActivityLogger.StepDurationRecord(
                        s.Id,
                        s.Title,
                        state,
                        end - s.StartTime,
                        s.FailureReason,
                        s.ParentStepId,
                        s.HierarchyLevel,
                        s.Number,
                        s.StartTime - earliestStartTime,
                        end - earliestStartTime);
                })
                .OrderBy(r => r.Sequence)
                .ToList();
                logger.SetStepDurations(durationRecords);

                // Provide final result to logger and print its structured summary.
                // Pass the pipeline summary if available for successful pipelines
                var pipelineSummary = !hasErrors ? publishingActivity.Data.PipelineSummary : null;
                logger.SetFinalResult(!hasErrors, pipelineSummary);
                logger.WriteSummary();

                // Visual bell
                Console.Write("\a");
                Console.Out.Flush();
                return !hasErrors;
            }

            return true;
        }
        finally
        {
            await logger.StopSpinnerAsync();
        }
    }

    private static string BuildPromptText(PublishingPromptInput input, int inputCount, string statusText, PublishingActivityData activityData)
    {
        if (inputCount > 1)
        {
            // Multi-input: just show the label with markdown conversion
            var labelText = ConvertTextWithMarkdownFlag($"{input.Label}: ", activityData);
            return labelText;
        }

        // Single-input: show both StatusText and Label
        var header = statusText ?? string.Empty;
        var label = input.Label ?? string.Empty;

        // If StatusText equals Label (case-insensitive), show only the label once
        if (header.Equals(label, StringComparison.OrdinalIgnoreCase))
        {
            return $"[bold]{ConvertTextWithMarkdownFlag(label, activityData)}[/]";
        }

        // Show StatusText as header (converted from markdown), then Label on new line
        var convertedHeader = ConvertTextWithMarkdownFlag(header, activityData);
        var convertedLabel = ConvertTextWithMarkdownFlag(label, activityData);
        return $"[bold]{convertedHeader}[/]\n{convertedLabel}: ";
    }

    private async Task HandlePromptActivityAsync(PublishingActivity activity, IAppHostCliBackchannel backchannel, CancellationToken cancellationToken)
    {
        if (activity.Data.IsComplete)
        {
            // Prompt is already completed, nothing to do
            return;
        }

        // Check if we have input information
        if (activity.Data.Inputs is not { Count: > 0 } inputs)
        {
            throw new InvalidOperationException("Prompt provided without input data.");
        }

        // Check for validation errors. If there are errors then this isn't the first time the user has been prompted.
        var hasValidationErrors = inputs.Any(input => input.ValidationErrors is { Count: > 0 });

        // For multiple inputs, display the activity status text as a header.
        // Don't display if there are validation errors. Validation errors means the header has already been displayed.
        if (!hasValidationErrors && inputs.Count > 1)
        {
            var headerText = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);
            AnsiConsole.MarkupLine($"[bold]{headerText}[/]");
        }

        // Handle multiple inputs
        var answers = new PublishingPromptInputAnswer[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];

            string? result;

            // Get prompt for input if there are no validation errors (first time we've asked)
            // or there are validation errors and this input has an error.
            if (!hasValidationErrors || input.ValidationErrors is { Count: > 0 })
            {
                // Build the prompt text based on number of inputs
                var promptText = BuildPromptText(input, inputs.Count, activity.Data.StatusText, activity.Data);

                result = await HandleSingleInputAsync(input, promptText, cancellationToken);
            }
            else
            {
                result = input.Value;
            }

            answers[i] = new PublishingPromptInputAnswer
            {
                Value = result
            };
        }

        // Send all results as an array
        await backchannel.CompletePromptResponseAsync(activity.Data.Id, answers, cancellationToken);
    }

    private async Task<string?> HandleSingleInputAsync(PublishingPromptInput input, string promptText, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<InputType>(input.InputType, ignoreCase: true, out var inputType))
        {
            // Fallback to text if unknown type
            inputType = InputType.Text;
        }

        // Display any validation errors.
        if (input.ValidationErrors is { Count: > 0 } errors)
        {
            foreach (var error in errors)
            {
                InteractionService.DisplayError(error);
            }
        }

        return inputType switch
        {
            InputType.Text => await InteractionService.PromptForStringAsync(
                promptText,
                defaultValue: input.Value?.EscapeMarkup(),
                required: input.Required,
                cancellationToken: cancellationToken),

            InputType.SecretText => await InteractionService.PromptForStringAsync(
                promptText,
                defaultValue: input.Value?.EscapeMarkup(),
                isSecret: true,
                required: input.Required,
                cancellationToken: cancellationToken),

            InputType.Choice => await HandleSelectInputAsync(input, promptText, cancellationToken),

            InputType.Boolean => (await InteractionService.ConfirmAsync(promptText, defaultValue: ParseBooleanValue(input.Value), cancellationToken: cancellationToken)).ToString().ToLowerInvariant(),

            InputType.Number => await HandleNumberInputAsync(input, promptText, cancellationToken),

            _ => await InteractionService.PromptForStringAsync(promptText, defaultValue: input.Value?.EscapeMarkup(), required: input.Required, cancellationToken: cancellationToken)
        };
    }

    private async Task<string?> HandleSelectInputAsync(PublishingPromptInput input, string promptText, CancellationToken cancellationToken)
    {
        if (input.Options is null || input.Options.Count == 0)
        {
            return await InteractionService.PromptForStringAsync(promptText, defaultValue: input.Value?.EscapeMarkup(), required: input.Required, cancellationToken: cancellationToken);
        }

        // If AllowCustomChoice is enabled then add an "Other" option to the list.
        // CLI doesn't support custom values directly in selection prompts. Instead an "Other" option is added.
        // If "Other" is selected then the user is prompted to enter a custom value as text.
        var options = input.Options.ToList();
        if (input.AllowCustomChoice)
        {
            options.Add(KeyValuePair.Create(CustomChoiceValue, InteractionServiceStrings.CustomChoiceLabel));
        }

        // For Choice inputs, we can't directly set a default in PromptForSelectionAsync,
        // but we can reorder the options to put the default first or use a different approach
        var (value, displayText) = await InteractionService.PromptForSelectionAsync(
            promptText,
            options,
            choice => choice.Value.EscapeMarkup(),
            cancellationToken);

        if (value == CustomChoiceValue)
        {
            return await InteractionService.PromptForStringAsync(promptText, defaultValue: input.Value?.EscapeMarkup(), required: input.Required, cancellationToken: cancellationToken);
        }

        AnsiConsole.MarkupLine($"{promptText} {displayText.EscapeMarkup()}");

        return value;
    }

    private async Task<string?> HandleNumberInputAsync(PublishingPromptInput input, string promptText, CancellationToken cancellationToken)
    {
        static ValidationResult Validator(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !double.TryParse(value, out _))
            {
                return ValidationResult.Error("Please enter a valid number.");
            }

            return ValidationResult.Success();
        }

        return await InteractionService.PromptForStringAsync(
            promptText,
            defaultValue: input.Value?.EscapeMarkup(),
            validator: Validator,
            required: input.Required,
            cancellationToken: cancellationToken);
    }

    private static bool ParseBooleanValue(string? value)
    {
        return bool.TryParse(value, out var result) && result;
    }

    private class StepInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int Number { get; set; }
        public string? ParentStepId { get; set; }
        public int HierarchyLevel { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string CompletionState { get; set; } = CompletionStates.InProgress;
        public string CompletionText { get; set; } = string.Empty;
        public string? FailureReason { get; set; }
        public Dictionary<string, TaskInfo> Tasks { get; } = [];
    }

    private class TaskInfo
    {
        public string Id { get; set; } = string.Empty;
        public string StatusText { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public string CompletionState { get; set; } = CompletionStates.InProgress;
        public string? CompletionMessage { get; set; }
    }

    // Removed legacy PublishingOutputRenderer and ProgressContextInfo (spinner & step coloring now handled by ConsoleActivityLogger).

    /// <summary>
    /// Starts the terminal infinite progress bar.
    /// </summary>
    private void StartTerminalProgressBar()
    {
        // Skip terminal progress bar in non-interactive environments
        if (!_hostEnvironment.SupportsInteractiveOutput)
        {
            return;
        }
        Console.Write("\u001b]9;4;3\u001b\\");
    }

    /// <summary>
    /// Stops the terminal progress bar.
    /// </summary>
    private void StopTerminalProgressBar()
    {
        // Skip terminal progress bar in non-interactive environments
        if (!_hostEnvironment.SupportsInteractiveOutput)
        {
            return;
        }
        Console.Write("\u001b]9;4;0\u001b\\");
    }
}
