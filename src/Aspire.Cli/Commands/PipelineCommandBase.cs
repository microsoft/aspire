// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Exceptions;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Cli.Utils.Markdown;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Semver;
using Spectre.Console;
using SystemCommandResult = System.CommandLine.Parsing.CommandResult;
using StreamJsonRpc;

namespace Aspire.Cli.Commands;

[JsonSerializable(typeof(PipelineInputsOutput))]
[JsonSerializable(typeof(PipelineInputOutput))]
[JsonSerializable(typeof(PipelineInputCliOutput))]
[JsonSerializable(typeof(PipelineInputEnvironmentOutput))]
[JsonSerializable(typeof(PipelineInputCurrentValueOutput))]
[JsonSerializable(typeof(PipelineInputValidationOutput))]
[JsonSerializable(typeof(PipelineInputOutput[]))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class PipelineCommandJsonContext : JsonSerializerContext
{
    private static PipelineCommandJsonContext? s_relaxedEscaping;

    public static PipelineCommandJsonContext RelaxedEscaping => s_relaxedEscaping ??= new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

internal sealed record PipelineInputsOutput(string Operation, string? Step, PipelineInputOutput[] Inputs);

internal sealed record PipelineInputOutput(
    string Name,
    string Kind,
    string? Group,
    string[] DependsOn,
    string Type,
    bool Required,
    string? Description,
    string? ConfigurationKey,
    PipelineInputEnvironmentOutput Environment,
    PipelineInputCliOutput Cli,
    PipelineInputCurrentValueOutput Current,
    PipelineInputValidationOutput? Validation);

internal sealed record PipelineInputCliOutput(string? Flag, string[] Aliases);

internal sealed record PipelineInputEnvironmentOutput(string Preferred, string[] Aliases);

internal sealed record PipelineInputCurrentValueOutput(bool HasValue, string? Value, string? Source);

internal sealed record PipelineInputValidationOutput(string[]? AllowedValues, bool AllowCustomChoice, int? MaxLength, bool DynamicallyLoaded);

internal abstract class PipelineCommandBase : BaseCommand
{
    protected override bool UpdateNotificationsEnabled => true;

    private const string CustomChoiceValue = "__CUSTOM_CHOICE";
    private const string ListStepsCapability = "pipeline-steps.v2";
    private const string InvalidListStepsFormatMessage = "The --format option requires either 'table' or 'json'.";
    private const string LegacyInspectOperationError = "Invalid operation specified. Valid operations are 'publish' or 'run'.";
    private const string ListStepsIncompatibleMessage = "The AppHost does not support --list-steps. Update the AppHost to a newer version of Aspire.";
    private const int MinimumHostingMajorVersionForListSteps = 13;
    private const int MinimumHostingMinorVersionForListSteps = 6;

    private bool _terminalProgressBarStarted;
    private const string PipelineInputsCapability = "pipeline-inputs.v1";
    private const string PipelineResourcesCapability = "pipeline-resources.v1";

    protected readonly IDotNetCliRunner _runner;
    protected readonly IProjectLocator _projectLocator;
    protected readonly IAppHostProjectFactory _projectFactory;

    private readonly IConfiguration _configuration;
    private readonly IFeatures _features;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly ILogger _logger;
    private readonly IAnsiConsole _ansiConsole;
    private bool _suppressTerminalProgressBar;

    protected static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", PublishCommandStrings.ProjectArgumentDescription);

    private readonly Option<string?> _outputPathOption;

    protected static readonly Option<string?> s_pipelineLogLevelOption = new("--pipeline-log-level")
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

    protected static readonly Option<bool> s_listStepsOption = new("--list-steps")
    {
        Description = SharedCommandStrings.PipelineListStepsOptionDescription
    };

    protected static readonly Option<bool> s_listInputsOption = new("--list-inputs")
    {
        Description = "List parameter-backed deployment inputs relevant to the target step, without running the pipeline. Runtime execution can still prompt for provider or custom step inputs that are not parameter-backed."
    };

    protected static readonly Option<bool> s_listResourcesOption = new("--list-resources")
    {
        Description = "List publish-mode resources known before pipeline execution, without running the pipeline."
    };

    protected static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = SharedCommandStrings.LsFormatOptionDescription
    };

    protected abstract string OperationCompletedPrefix { get; }
    protected abstract string OperationFailedPrefix { get; }

    private static bool IsCompletionStateComplete(string completionState) =>
        completionState is CompletionStates.Completed or CompletionStates.CompletedWithWarning or CompletionStates.CompletedWithError;

    private static bool IsCompletionStateError(string completionState) =>
        completionState == CompletionStates.CompletedWithError;

    private static bool IsCompletionStateWarning(string completionState) =>
        completionState == CompletionStates.CompletedWithWarning;

    protected PipelineCommandBase(string name, string description, IDotNetCliRunner runner, IProjectLocator projectLocator, IFeatures features, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory, IConfiguration configuration, ILogger logger, IAnsiConsole ansiConsole, CommonCommandServices services)
        : base(name, description, services)
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
        Options.Add(s_pipelineLogLevelOption);
        Options.Add(s_environmentOption);
        Options.Add(s_includeExceptionDetailsOption);
        Options.Add(s_noBuildOption);
        Options.Add(s_listStepsOption);
        Options.Add(s_listInputsOption);
        Options.Add(s_listResourcesOption);
        Options.Add(new HelpOption { Action = new PipelineCommandHelpAction(this) });

        Validators.Add(result =>
        {
            if (GetListOptionCount(result) > 1)
            {
                result.AddError("The '--list-steps', '--list-inputs', and '--list-resources' options cannot be used together.");
            }
        });

        // In the publish and deploy commands we forward all unrecognized tokens
        // through to the underlying tooling when we launch the app host.
        TreatUnmatchedTokensAsErrors = false;
    }

    protected abstract string GetOutputPathDescription();
    protected abstract Task<string[]> GetRunArgumentsAsync(string? fullyQualifiedOutputPath, string[] unmatchedTokens, string? targetStep, ParseResult parseResult, CancellationToken cancellationToken);
    protected abstract string GetCanceledMessage();
    protected abstract string GetProgressMessage(ParseResult parseResult);

    /// <summary>
    /// Gets the target pipeline step name for this invocation.
    /// In list mode, a null target shows all steps.
    /// </summary>
    protected virtual string? GetTargetStepName(ParseResult parseResult) => null;

    /// <summary>
    /// Gets command-specific arguments to forward when starting a debug session from the extension context.
    /// Subclasses should override to include their specific positional arguments.
    /// Unmatched tokens are always included automatically.
    /// </summary>
    protected virtual string[] GetCommandArgs(ParseResult parseResult) => [];

    protected override bool IsJsonFormatRequested(ParseResult parseResult)
    {
        return base.IsJsonFormatRequested(parseResult) ||
            (IsListOperation(parseResult) &&
             TryResolveListStepsInvocation(parseResult, out var outputFormat, out _, out _) &&
             outputFormat is OutputFormat.Json);
    }

    private string GetUsageSyntax()
    {
        var argumentSyntax = GetArgumentSyntax();
        var commandAndArguments = string.IsNullOrEmpty(argumentSyntax)
            ? Name
            : $"{Name} {argumentSyntax}";

        return $"aspire {commandAndArguments} [options] [[--] <pipeline-input-arguments>...]";
    }

    private string GetArgumentSyntax()
    {
        if (Arguments.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var argument in Arguments)
        {
            if (argument.Hidden)
            {
                continue;
            }

            var name = $"<{argument.Name}>";
            if (argument.Arity.MinimumNumberOfValues == 0)
            {
                name = $"[{name}]";
            }

            parts.Add(name);
        }

        return string.Join(" ", parts);
    }

    private sealed class PipelineCommandHelpAction(PipelineCommandBase command) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            WritePipelineCommandHelp(parseResult.InvocationConfiguration.Output, parseResult.CommandResult);
            return CliExitCodes.Success;
        }

        private void WritePipelineCommandHelp(TextWriter writer, SystemCommandResult commandResult)
        {
            if (!string.IsNullOrEmpty(command.Description))
            {
                writer.WriteLine("Description:");
                writer.WriteLine($"  {command.Description}");
                writer.WriteLine();
            }

            GroupedHelpWriter.WriteUsage(writer, command.GetUsageSyntax());

            if (command.Arguments.Count > 0)
            {
                GroupedHelpWriter.WriteTwoColumnSection(
                    writer,
                    "Arguments:",
                    command.Arguments
                        .Where(static argument => !argument.Hidden)
                        .Select(static argument => (GetArgumentLabel(argument), argument.Description ?? string.Empty)),
                    maxWidth: command._ansiConsole.Profile.Width);
            }

            GroupedHelpWriter.WriteTwoColumnSection(
                writer,
                HelpGroupStrings.Options,
                GetVisibleOptionRows(commandResult),
                maxWidth: command._ansiConsole.Profile.Width);

            GroupedHelpWriter.WriteTwoColumnSection(
                writer,
                "Pipeline input arguments:",
                [("--<input-name> <value>", "Supplies parameter-backed pipeline inputs discovered with --list-inputs. Use -- before input arguments when an input flag conflicts with an Aspire CLI option.")],
                maxWidth: command._ansiConsole.Profile.Width,
                trailingBlankLine: false);
        }

        private static IEnumerable<(string Label, string Description)> GetVisibleOptionRows(SystemCommandResult commandResult)
        {
            yield return (GetOptionLabel(s_formatOption), "Output format for --list-steps, --list-inputs, or --list-resources.");
            var seenLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in commandResult.Command.Options)
            {
                if (!option.Hidden && TryGetOptionRow(option, seenLabels, out var row))
                {
                    yield return row;
                }
            }

            var current = commandResult.Parent;
            while (current is SystemCommandResult parentCommandResult)
            {
                foreach (var option in parentCommandResult.Command.Options)
                {
                    if (option.Recursive && !option.Hidden && TryGetOptionRow(option, seenLabels, out var row))
                    {
                        yield return row;
                    }
                }

                current = parentCommandResult.Parent;
            }
        }

        private static bool TryGetOptionRow(Option option, HashSet<string> seenLabels, out (string Label, string Description) row)
        {
            var label = GetOptionLabel(option);
            row = (label, option.Description ?? string.Empty);
            return seenLabels.Add(label);
        }

        private static string GetOptionLabel(Option option)
        {
            var label = GroupedHelpWriter.FormatOptionLabel(option);
            return option switch
            {
                Option<OutputFormat> => $"{label} <{GetEnumValueLabel<OutputFormat>()}>",
                Option<LogLevel?> => $"{label} <{GetEnumValueLabel<LogLevel>()}>",
                _ => GroupedHelpWriter.FormatOptionLabel(option, includeValueName: true)
            };
        }

        private static string GetEnumValueLabel<TEnum>() where TEnum : struct, Enum =>
            string.Join('|', Enum.GetNames<TEnum>().Order(StringComparer.Ordinal));

        private static string GetArgumentLabel(Argument argument)
        {
            var label = $"<{argument.Name}>";
            return argument.Arity.MinimumNumberOfValues == 0 ? $"[{label}]" : label;
        }
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // If running in the extension context (Aspire terminal) without a debug session,
        // intercept and tell VS Code to start a proper debug session for this command.
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var explicitAppHost = passedAppHostProjectFile is not null;
        var listSteps = parseResult.GetValue(s_listStepsOption);
        var listOperation = IsListOperation(parseResult);
        var unmatchedTokens = parseResult.UnmatchedTokens.ToArray();
        var targetStep = GetTargetStepName(parseResult);
        var outputFormat = OutputFormat.Table;
        if (listOperation && !TryResolveListStepsInvocation(parseResult, out outputFormat, out targetStep, out unmatchedTokens))
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, InvalidListStepsFormatMessage);
        }

        _suppressTerminalProgressBar = listOperation && outputFormat is OutputFormat.Json;

        if (ExtensionHelper.IsExtensionHost(InteractionService, out var extensionInteractionService, out _)
            && string.IsNullOrEmpty(_configuration[KnownConfigNames.ExtensionDebugSessionId])
            && !listOperation)
        {
            // Resolve the apphost project interactively before starting the debug session,
            // so the user is prompted if needed and we can pass it along.
            if (passedAppHostProjectFile is null)
            {
                var searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, MultipleAppHostProjectsFoundBehavior.Prompt, createSettingsFile: true, cancellationToken);
                passedAppHostProjectFile = searchResult.SelectedProjectFile;

                if (passedAppHostProjectFile is null)
                {
                    return CommandResult.Failure(CliExitCodes.FailedToFindProject);
                }
            }

            var commandArgs = GetCommandArgs(parseResult).Concat(parseResult.UnmatchedTokens).ToArray();

            extensionInteractionService.DisplayConsolePlainText($"Detected aspire {Name} inside the Aspire extension, starting a debug session in VS Code...");
            await extensionInteractionService.StartDebugSessionAsync(
                ExecutionContext.WorkingDirectory.FullName,
                passedAppHostProjectFile?.FullName,
                debug: true,
                new DebugSessionOptions
                {
                    Command = Name,
                    Args = commandArgs.Length > 0 ? commandArgs : null,
                    AppHostSelectionOrigin = explicitAppHost
                        ? DebugSessionOptions.ExplicitCliAppHostSelectionOrigin
                        : DebugSessionOptions.DefaultDiscoveryAppHostSelectionOrigin,
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        [KnownConfigNames.AspireHome] = ExecutionContext.AspireHomeDirectory.FullName
                    }
                });
            return CommandResult.Success();
        }

        var debugMode = parseResult.GetValue(RootCommand.DebugOption);
        var waitForDebugger = parseResult.GetValue(RootCommand.WaitForDebuggerOption);
        var noBuild = parseResult.GetValue(s_noBuildOption);
        var startDebugSession = ExtensionHelper.IsExtensionHost(InteractionService, out _, out _) && parseResult.GetValue(RootCommand.StartDebugSessionOption);

        Task<int>? pendingRun = null;
        PublishContext? publishContext = null;

        // Machine-readable output must not contain terminal control sequences.
        if (!_suppressTerminalProgressBar)
        {
            StartTerminalProgressBar();
        }

        try
        {
            using var activity = Telemetry.StartDiagnosticActivity(this.Name);

            var searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, MultipleAppHostProjectsFoundBehavior.Prompt, createSettingsFile: true, cancellationToken);
            var effectiveAppHostFile = searchResult.SelectedProjectFile;

            if (effectiveAppHostFile is null)
            {
                // Send terminal progress bar stop sequence
                StopTerminalProgressBar();
                return CommandResult.Failure(CliExitCodes.FailedToFindProject);
            }

            var project = _projectFactory.GetProject(effectiveAppHostFile);
            if (listOperation)
            {
                var aspireHostingVersion = await project.GetAspireHostingVersionAsync(effectiveAppHostFile, cancellationToken);
                if (IsKnownIncompatibleWithListSteps(aspireHostingVersion))
                {
                    throw new AppHostIncompatibleException(
                        ListStepsIncompatibleMessage,
                        ListStepsCapability,
                        aspireHostingVersion);
                }
            }

            var env = new Dictionary<string, string>
            {
                [KnownConfigNames.AspireHome] = ExecutionContext.AspireHomeDirectory.FullName
            };

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

            var runArguments = await GetRunArgumentsAsync(fullyQualifiedOutputPath, unmatchedTokens, targetStep, parseResult, cancellationToken);

            if (listOperation)
            {
                runArguments = [.. runArguments, "--operation", "inspect", "--list-steps", "true"];
            }

            // Create the publish context and delegate to IAppHostProject
            publishContext = new PublishContext
            {
                AppHostFile = effectiveAppHostFile,
                OutputPath = fullyQualifiedOutputPath,
                EnvironmentVariables = env,
                Arguments = runArguments,
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

            var backchannel = await InteractionService.ShowStatusAsync(GetProgressMessage(parseResult), (Func<Task<IAppHostCliBackchannel>>)(async () =>
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
                if (!completedTask.IsFaulted && await pendingRun == CliExitCodes.Success
                    && ExtensionHelper.IsExtensionHost(InteractionService, out _, out _))
                {
                    return await backchannelCompletionSource.Task;
                }

                // Throw an error if the run completed without returning a backchannel.
                // Include possible error if the run task faulted.
                var innerException = completedTask.IsFaulted ? completedTask.Exception : null;
                throw new InvalidOperationException("Run completed without returning a backchannel.", innerException);
            }), emoji: KnownEmojis.HammerAndWrench);

            // Inspection must stop the AppHost even when metadata resolution fails.
            if (listOperation)
            {
                StopTerminalProgressBar();
                int inspectionExitCode;

                try
                {
                    // Check that the AppHost supports this capability before calling
                    var capabilities = await backchannel.GetCapabilitiesAsync(cancellationToken);
                    if (!capabilities.Contains(ListStepsCapability))
                    {
                        throw new AppHostIncompatibleException(
                            ListStepsIncompatibleMessage,
                            ListStepsCapability);
                    }

                    if (listSteps)
                    {
                        var response = await backchannel.GetPipelineStepsAsync(targetStep, cancellationToken);
                        PrintPipelineSteps(response.Steps, outputFormat);
                    }
                    else if (parseResult.GetValue(s_listInputsOption))
                    {
                        await EnsureCapabilityAsync(backchannel, PipelineInputsCapability, "--list-inputs", cancellationToken).ConfigureAwait(false);
                        var response = await backchannel.GetPipelineInputsAsync(targetStep, cancellationToken).ConfigureAwait(false);
                        PrintPipelineInputs(response.Inputs, outputFormat, targetStep);
                    }
                    else
                    {
                        await EnsureCapabilityAsync(backchannel, PipelineResourcesCapability, "--list-resources", cancellationToken).ConfigureAwait(false);
                        var response = await backchannel.GetPipelineResourcesAsync(includeHidden: false, cancellationToken).ConfigureAwait(false);
                        PrintPipelineResources(response.Resources, outputFormat);
                    }
                }
                finally
                {
                    try
                    {
                        await backchannel.RequestStopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        inspectionExitCode = await pendingRun;
                    }
                }

                return CommandResult.FromExitCode(inspectionExitCode);
            }

            var pipelineParameterArguments = PipelineParameterArguments.Empty;
            var pipelineCapabilities = await backchannel.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (pipelineCapabilities.Contains(PipelineInputsCapability, StringComparer.Ordinal))
            {
                var inputsResponse = await backchannel.GetPipelineInputsAsync(targetStep, cancellationToken).ConfigureAwait(false);
                var parsedArguments = CreatePipelineParameterArguments(inputsResponse.Inputs, unmatchedTokens, requireMissingInputs: parseResult.GetValue(RootCommand.NonInteractiveOption) || !_hostEnvironment.SupportsInteractiveInput);
                if (parsedArguments.ErrorMessage is { } errorMessage)
                {
                    StopTerminalProgressBar();
                    await backchannel.RequestStopAsync(cancellationToken).ConfigureAwait(false);
                    await pendingRun;
                    return CommandResult.Failure(CliExitCodes.InvalidCommand, errorMessage);
                }

                pipelineParameterArguments = parsedArguments.Arguments;
                if (pipelineParameterArguments.Values.Count > 0)
                {
                    await backchannel.ApplyPipelineInputValuesAsync(pipelineParameterArguments.Values, cancellationToken).ConfigureAwait(false);
                }
            }

            var publishingActivities = backchannel.GetPublishingActivitiesAsync(cancellationToken);

            // Check if debug or trace logging is enabled
            var logLevel = parseResult.GetValue(s_pipelineLogLevelOption);
            var isDebugOrTraceLoggingEnabled = logLevel?.Equals("debug", StringComparison.OrdinalIgnoreCase) == true ||
                                                 logLevel?.Equals("trace", StringComparison.OrdinalIgnoreCase) == true;

            var noFailuresReported = debugMode switch
            {
                true => await ProcessPublishingActivitiesDebugAsync(publishingActivities, backchannel, pipelineParameterArguments, cancellationToken),
                false => await ProcessAndDisplayPublishingActivitiesAsync(publishingActivities, backchannel, pipelineParameterArguments, isDebugOrTraceLoggingEnabled, cancellationToken),
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
                return CommandResult.FromExitCode(exitCode);
            }

            // If the apphost exited successfully (0) but reported failures via backchannel,
            // return a failure exit code.
            if (!noFailuresReported)
            {
                return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts);
            }

            // Both apphost exit code and backchannel indicate success
            return CommandResult.Success();
        }
        catch (ExtensionOperationCanceledException)
        {
            // BaseCommand handles extension prompt dismissal without displaying an error notification.
            StopTerminalProgressBar();
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Send terminal progress bar stop sequence on cancellation
            StopTerminalProgressBar();
            _logger.LogDebug(ex, "Operation was cancelled.");
            var canceledMessage = GetCanceledMessage();
            Telemetry.RecordError(canceledMessage, ex);
            return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts, canceledMessage);
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
            return CommandResult.Failure(CliExitCodes.SdkNotInstalled);
        }
        catch (AppHostIncompatibleException ex)
        {
            // Send terminal progress bar stop sequence on exception
            StopTerminalProgressBar();
            Telemetry.RecordError($"AppHost is incompatible. Required capability: {ex.RequiredCapability}", ex);
            return CommandResult.Failure(CliExitCodes.AppHostIncompatible, ex.Message);
        }
        catch (Exception ex) when (listOperation && HasLegacyInspectOperationError(publishContext?.OutputCollector))
        {
            StopTerminalProgressBar();
            Telemetry.RecordError($"AppHost is incompatible. Required capability: {ListStepsCapability}", ex);
            return CommandResult.Failure(CliExitCodes.AppHostIncompatible, ListStepsIncompatibleMessage);
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
            return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts);
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
            return CommandResult.FromExitCode(pendingRun is { } && debugMode ? await pendingRun : CliExitCodes.FailedToBuildArtifacts);
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
            return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts);
        }
    }

    private static bool IsKnownIncompatibleWithListSteps(string? aspireHostingVersion)
    {
        if (string.IsNullOrWhiteSpace(aspireHostingVersion) ||
            !SemVersion.TryParse(aspireHostingVersion, SemVersionStyles.Any, out var version))
        {
            return false;
        }

        return version.Major < MinimumHostingMajorVersionForListSteps ||
            (version.Major == MinimumHostingMajorVersionForListSteps &&
             version.Minor < MinimumHostingMinorVersionForListSteps);
    }

    private static bool TryExtractListStepsFormat(
        IReadOnlyList<string> unmatchedTokens,
        out OutputFormat format,
        out string[] remainingTokens)
    {
        format = OutputFormat.Table;
        var remaining = new List<string>(unmatchedTokens.Count);

        for (var i = 0; i < unmatchedTokens.Count; i++)
        {
            var token = unmatchedTokens[i];
            string value;

            if (token == "--format")
            {
                if (++i >= unmatchedTokens.Count)
                {
                    remainingTokens = [];
                    return false;
                }

                value = unmatchedTokens[i];
            }
            else if (token.StartsWith("--format=", StringComparison.Ordinal))
            {
                value = token["--format=".Length..];
            }
            else
            {
                remaining.Add(token);
                continue;
            }

            if (value.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                format = OutputFormat.Json;
            }
            else if (value.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                format = OutputFormat.Table;
            }
            else
            {
                remainingTokens = [];
                return false;
            }
        }

        remainingTokens = [.. remaining];
        return true;
    }

    private bool TryResolveListStepsInvocation(
        ParseResult parseResult,
        out OutputFormat outputFormat,
        out string? targetStep,
        out string[] unmatchedTokens)
    {
        targetStep = GetTargetStepName(parseResult);
        unmatchedTokens = parseResult.UnmatchedTokens.ToArray();

        // System.CommandLine can bind the unregistered --format token to `do`'s optional step.
        // Put it back before parsing the list-only option, then recover the actual positional step
        // from the tokens that remain after the format pair is removed.
        var formatTokenParsedAsTarget = targetStep is not null && IsFormatToken(targetStep);
        if (formatTokenParsedAsTarget)
        {
            unmatchedTokens = [targetStep!, .. unmatchedTokens];
            targetStep = null;
        }

        if (!TryExtractListStepsFormat(unmatchedTokens, out outputFormat, out unmatchedTokens))
        {
            return false;
        }

        if (formatTokenParsedAsTarget)
        {
            targetStep = ExtractPositionalTarget(unmatchedTokens, out unmatchedTokens);
        }

        return true;
    }

    private static bool IsFormatToken(string token) =>
        token == "--format" || token.StartsWith("--format=", StringComparison.Ordinal);

    private static string? ExtractPositionalTarget(string[] tokens, out string[] remainingTokens)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].StartsWith("-", StringComparison.Ordinal))
            {
                remainingTokens = [.. tokens[..i], .. tokens[(i + 1)..]];
                return tokens[i];
            }
        }

        remainingTokens = tokens;
        return null;
    }

    private static bool HasLegacyInspectOperationError(OutputCollector? outputCollector) =>
        outputCollector?.GetLines().Any(line =>
            line.Line.Contains(LegacyInspectOperationError, StringComparison.Ordinal)) == true;

    protected static bool IsListOperation(SystemCommandResult commandResult) => GetListOptionCount(commandResult) > 0;

    protected static bool IsListOperation(ParseResult parseResult) =>
        parseResult.GetValue(s_listStepsOption) ||
        parseResult.GetValue(s_listInputsOption) ||
        parseResult.GetValue(s_listResourcesOption);

    protected static string? GetListProgressMessage(ParseResult parseResult)
    {
        if (parseResult.GetValue(s_listStepsOption))
        {
            return "Listing pipeline steps";
        }

        if (parseResult.GetValue(s_listInputsOption))
        {
            return "Listing pipeline inputs";
        }

        if (parseResult.GetValue(s_listResourcesOption))
        {
            return "Listing pipeline resources";
        }

        return null;
    }

    private static int GetListOptionCount(SystemCommandResult commandResult)
    {
        var listOptions = 0;
        listOptions += commandResult.GetValue(s_listStepsOption) ? 1 : 0;
        listOptions += commandResult.GetValue(s_listInputsOption) ? 1 : 0;
        listOptions += commandResult.GetValue(s_listResourcesOption) ? 1 : 0;

        return listOptions;
    }

    private static async Task EnsureCapabilityAsync(IAppHostCliBackchannel backchannel, string capability, string optionName, CancellationToken cancellationToken)
    {
        var capabilities = await backchannel.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.Contains(capability, StringComparer.Ordinal))
        {
            throw new AppHostIncompatibleException(
                $"The AppHost does not support {optionName}. Update the AppHost to a newer version of Aspire.",
                capability);
        }
    }

    /// <summary>
    /// Prints pipeline steps in a numbered tree format showing dependencies and tags.
    /// </summary>
    internal void PrintPipelineSteps(PipelineStepInfo[] steps, OutputFormat format = default)
    {
        if (format == OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(steps, JsonSourceGenerationContext.RelaxedEscaping.PipelineStepInfoArray);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
            return;
        }

        if (steps.Length == 0)
        {
            _ansiConsole.MarkupLine("[dim]No pipeline steps found.[/]");
            return;
        }

        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];

            _ansiConsole.MarkupLine($"[bold green]{i + 1}.[/] [cyan]{step.Name.EscapeMarkup()}[/]");

            var hasDeps = step.DependsOn.Length > 0;
            var hasTags = step.Tags.Length > 0;

            if (!hasDeps && !hasTags)
            {
                _ansiConsole.MarkupLine("[dim]   └─ No dependencies[/]");
            }
            else
            {
                if (hasDeps)
                {
                    var connector = hasTags ? "├" : "└";
                    var continuation = hasTags ? "│" : " ";
                    var firstLinePrefix = $"   {connector}─ [blue]Depends on:[/] ";
                    // Build continuation prefix that aligns wrapped items under the first dep value.
                    // Replace the connector with the continuation char and pad the rest with spaces.
                    var visibleWidth = StripMarkup(firstLinePrefix).Length;
                    var continuationPrefix = "   " + continuation + new string(' ', visibleWidth - 4);
                    var wrappedDeps = FormatWithHangingIndent(step.DependsOn, firstLinePrefix, continuationPrefix);
                    _ansiConsole.MarkupLine(wrappedDeps);
                }

                if (hasTags)
                {
                    var tagsText = string.Join(", ", step.Tags);
                    _ansiConsole.MarkupLine($"   └─ [yellow]Tags:[/] {tagsText.EscapeMarkup()}");
                }
            }

            if (i < steps.Length - 1)
            {
                _ansiConsole.WriteLine();
            }
        }
    }

    internal void PrintPipelineResources(ResourceSnapshot[] resources, OutputFormat format)
    {
        if (format == OutputFormat.Json)
        {
            var output = new ResourcesOutput { Resources = ResourceSnapshotMapper.MapToResourceJsonList(resources).ToArray() };
            var json = JsonSerializer.Serialize(output, ResourcesCommandJsonContext.RelaxedEscaping.ResourcesOutput);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
            return;
        }

        if (resources.Length == 0)
        {
            _ansiConsole.MarkupLine("[dim]No publish-mode resources found.[/]");
            return;
        }

        var orderedItems = resources
            .Select(resource => (Snapshot: resource, DisplayName: ResourceSnapshotMapper.GetResourceName(resource, resources)))
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var table = new Table();
        table.AddBoldColumn(DescribeCommandStrings.HeaderName);
        table.AddBoldColumn(DescribeCommandStrings.HeaderType);
        table.AddBoldColumn(DescribeCommandStrings.HeaderState);
        table.AddBoldColumn(DescribeCommandStrings.HeaderHealth);
        table.AddBoldColumn(DescribeCommandStrings.HeaderURLs);

        foreach (var (snapshot, displayName) in orderedItems)
        {
            var endpoints = snapshot.Urls.Length > 0
                ? string.Join(", ", snapshot.Urls.Where(static url => !url.IsInternal).Select(static url => url.DisplayProperties?.DisplayName ?? url.Url))
                : "-";

            table.AddRow(
                displayName,
                snapshot.ResourceType ?? "-",
                snapshot.State ?? "-",
                snapshot.HealthStatus ?? "-",
                endpoints);
        }

        _ansiConsole.Write(table);
    }

    internal void PrintPipelineInputs(PipelineInput[] inputs, OutputFormat format, string? step)
    {
        var output = new PipelineInputsOutput(Name, step, [.. inputs.Select(CreatePipelineInputOutput)]);

        if (format == OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(output, PipelineCommandJsonContext.RelaxedEscaping.PipelineInputsOutput);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
            return;
        }

        if (inputs.Length == 0)
        {
            _ansiConsole.MarkupLine("[dim]No deployment inputs found.[/]");
            return;
        }

        var table = new Table()
            .AddColumn("Name")
            .AddColumn("Type")
            .AddColumn("Required")
            .AddColumn("Has value")
            .AddColumn("Flag")
            .AddColumn("Environment");

        foreach (var input in output.Inputs)
        {
            table.AddRow(
                input.Name,
                input.Type,
                input.Required ? "Yes" : "No",
                input.Current.HasValue ? "Yes" : "No",
                input.Cli.Flag ?? string.Empty,
                input.Environment.Preferred);
        }

        _ansiConsole.Write(table);

        var inputsWithEnvironmentAliases = output.Inputs
            .Where(static input => input.Environment.Aliases.Length > 0)
            .ToArray();

        if (inputsWithEnvironmentAliases.Length > 0)
        {
            _ansiConsole.WriteLine();

            var aliasesTable = new Table()
                .AddColumn("Name")
                .AddColumn("Accepted environment aliases");

            foreach (var input in inputsWithEnvironmentAliases)
            {
                aliasesTable.AddRow(input.Name, string.Join(Environment.NewLine, input.Environment.Aliases));
            }

            _ansiConsole.Write(aliasesTable);
        }
    }

    private static PipelineInputOutput CreatePipelineInputOutput(PipelineInput input)
    {
        var optionName = CommandInputParser.ToKebabCase(input.Name);
        var flag = $"--{optionName}";
        var configurationKey = input.ConfigurationKey ?? $"Parameters:{input.Name}";
        var aliases = new List<string> { flag };

        AddAliasIfDifferent(aliases, $"--{input.Name}", flag);
        AddAliasIfDifferent(aliases, $"--Parameters:{input.Name}", flag);
        AddAliasIfDifferent(aliases, $"--ConnectionStrings:{input.Name}", flag);

        var validation = input.Options is { Count: > 0 } || input.MaxLength is not null || input.DynamicallyLoaded
            ? new PipelineInputValidationOutput(
                input.Options is { Count: > 0 } ? [.. input.Options.Keys] : null,
                input.AllowCustomChoice,
                input.MaxLength,
                input.DynamicallyLoaded)
            : null;

        return new PipelineInputOutput(
            input.Name,
            input.Kind,
            input.Group,
            input.DependsOn,
            input.InputType,
            input.Required,
            input.Description ?? input.Label,
            configurationKey,
            CreateEnvironmentOutput(configurationKey),
            new PipelineInputCliOutput(CommandInputParser.IsBooleanInput(input.InputType) ? flag : $"{flag} <value>", [.. aliases]),
            new PipelineInputCurrentValueOutput(input.HasValue || input.Value is not null, input.Value, input.ValueSource),
            validation);
    }

    private static void AddAliasIfDifferent(List<string> aliases, string alias, string flag)
    {
        if (!string.Equals(alias, flag, StringComparison.Ordinal))
        {
            aliases.Add(alias);
        }
    }

    private static PipelineInputEnvironmentOutput CreateEnvironmentOutput(string configurationKey)
    {
        var exactEnvironmentVariable = configurationKey.Replace(":", "__", StringComparison.Ordinal);
        var normalizedEnvironmentVariable = exactEnvironmentVariable.Replace("-", "_", StringComparison.Ordinal);
        var preferredEnvironmentVariable = normalizedEnvironmentVariable.ToUpperInvariant();

        var aliases = new List<string>();
        AddEnvironmentAliasIfDifferent(aliases, normalizedEnvironmentVariable, preferredEnvironmentVariable);
        AddEnvironmentAliasIfDifferent(aliases, exactEnvironmentVariable, preferredEnvironmentVariable);

        return new PipelineInputEnvironmentOutput(preferredEnvironmentVariable, [.. aliases]);
    }

    private static void AddEnvironmentAliasIfDifferent(List<string> aliases, string alias, string preferred)
    {
        if (!string.Equals(alias, preferred, StringComparison.Ordinal) &&
            !aliases.Contains(alias, StringComparer.Ordinal))
        {
            aliases.Add(alias);
        }
    }

    /// <summary>
    /// Formats a list of items with a prefix on the first line and hanging indent on continuation lines.
    /// Items are comma-separated and wrapped so each line stays readable.
    /// </summary>
    private static string FormatWithHangingIndent(string[] items, string firstLinePrefix, string continuationPrefix, int maxLineLength = 100)
    {
        if (items.Length == 0)
        {
            return firstLinePrefix;
        }

        var sb = new StringBuilder();
        sb.Append(firstLinePrefix);

        // Track visible length (without markup tags) for line wrapping
        var visiblePrefixLength = StripMarkup(firstLinePrefix).Length;
        var currentLineLength = visiblePrefixLength;

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i].EscapeMarkup();
            var separator = i < items.Length - 1 ? ", " : "";
            var chunk = item + separator;

            if (i > 0 && currentLineLength + chunk.Length > maxLineLength)
            {
                sb.AppendLine();
                sb.Append(continuationPrefix);
                currentLineLength = StripMarkup(continuationPrefix).Length;
            }

            sb.Append(chunk);
            currentLineLength += chunk.Length;
        }

        return sb.ToString();
    }

    private static string StripMarkup(string text)
    {
        // Remove Spectre markup tags like [bold], [/], [blue], etc.
        return System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[^\]]*\]", "");
    }

    private static (PipelineParameterArguments Arguments, string? ErrorMessage) CreatePipelineParameterArguments(PipelineInput[] inputs, string[] capturedArguments, bool requireMissingInputs)
    {
        capturedArguments = CommandInputParser.RemoveDelimiter(capturedArguments);

        if (capturedArguments.Length == 0)
        {
            var requiredInputs = requireMissingInputs
                ? inputs.Where(static input => input.Required && string.IsNullOrEmpty(input.Value)).ToArray()
                : [];

            if (requiredInputs.Length == 0)
            {
                return (PipelineParameterArguments.Empty, null);
            }
        }

        if (inputs.Length == 0)
        {
            return (PipelineParameterArguments.Empty, null);
        }

        var parseResult = CommandInputParser.Parse(inputs, capturedArguments, requireMissingInputs);
        if (parseResult.ErrorMessage is { } errorMessage)
        {
            return (PipelineParameterArguments.Empty, errorMessage);
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (argumentName, value) in parseResult.Arguments)
        {
            if (value is null)
            {
                continue;
            }

            values[argumentName] = value.GetValue<string?>();
        }

        return (values.Count > 0 ? new PipelineParameterArguments(values) : PipelineParameterArguments.Empty, null);
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

    public async Task<bool> ProcessPublishingActivitiesDebugAsync(IAsyncEnumerable<PublishingActivity> publishingActivities, IAppHostCliBackchannel backchannel, PipelineParameterArguments pipelineParameterArguments, CancellationToken cancellationToken)
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
                await HandlePromptActivityAsync(activity, backchannel, pipelineParameterArguments, cancellationToken);
            }
            else if (activity.Type == PublishingActivityTypes.Log)
            {
                // Log activity - display the log message
                var (parsedLogLevel, logPrefix) = ParseLogLevel(activity.Data.LogLevel);
                var message = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);
                var timestamp = activity.Data.Timestamp?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? DateTimeOffset.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

                // Make debug and trace logs more subtle
                var formattedMessage = parsedLogLevel switch
                {
                    LogLevel.Debug or LogLevel.Trace => $"[[{timestamp}]] [dim][[{logPrefix}]] {message}[/]",
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

    public async Task<bool> ProcessAndDisplayPublishingActivitiesAsync(IAsyncEnumerable<PublishingActivity> publishingActivities, IAppHostCliBackchannel backchannel, PipelineParameterArguments pipelineParameterArguments, bool isDebugOrTraceLoggingEnabled, CancellationToken cancellationToken)
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
                    await HandlePromptActivityAsync(activity, backchannel, pipelineParameterArguments, cancellationToken);
                    logger.StartSpinner();
                }
                else if (activity.Type == PublishingActivityTypes.Log)
                {
                    // Log activity - display through logger based on log level
                    var stepId = activity.Data.StepId;
                    if (stepId != null && steps.TryGetValue(stepId, out var stepInfo))
                    {
                        var (parsedLogLevel, logPrefix) = ParseLogLevel(activity.Data.LogLevel);
                        var message = ConvertTextWithMarkdownFlag(activity.Data.StatusText, activity.Data);

                        var prefixedMessage = $"[[{logPrefix}]] {message}";

                        switch (parsedLogLevel)
                        {
                            case LogLevel.Error:
                            case LogLevel.Critical:
                                logger.Failure(stepInfo.Id, prefixedMessage);
                                break;
                            case LogLevel.Warning:
                                logger.Warning(stepInfo.Id, prefixedMessage);
                                break;
                            case LogLevel.Debug:
                            case LogLevel.Trace:
                                logger.Info(stepInfo.Id, prefixedMessage, dim: true);
                                break;
                            case LogLevel.Information:
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

    private async Task HandlePromptActivityAsync(PublishingActivity activity, IAppHostCliBackchannel backchannel, PipelineParameterArguments pipelineParameterArguments, CancellationToken cancellationToken)
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
            if (!hasValidationErrors &&
                input.Name is { } inputName &&
                pipelineParameterArguments.Values.TryGetValue(inputName, out var suppliedValue))
            {
                result = suppliedValue;
            }
            else if (!hasValidationErrors || input.ValidationErrors is { Count: > 0 })
            {
                // Build the prompt text based on number of inputs
                var promptText = BuildPromptText(input, inputs.Count, activity.Data.StatusText, activity.Data);

                result = await HandleSingleInputAsync(input, promptText, backchannel, activity.Data.Id, cancellationToken);
            }
            else
            {
                result = input.Value;
            }

            answers[i] = new PublishingPromptInputAnswer
            {
                Name = input.Name,
                Value = result
            };
        }

        // Send all results as an array
        await backchannel.CompletePromptResponseAsync(activity.Data.Id, answers, cancellationToken);
    }

    private async Task<string?> HandleSingleInputAsync(PublishingPromptInput input, string promptText, IAppHostCliBackchannel backchannel, string interactionId, CancellationToken cancellationToken)
    {
        // The wire format uses hyphens (e.g. "secret-text") but the enum uses PascalCase (SecretText).
        var normalizedType = input.InputType.Replace("-", "", StringComparison.Ordinal);
        if (!Enum.TryParse<InputType>(normalizedType, ignoreCase: true, out var inputType))
        {
            throw new InvalidOperationException($"Unsupported input type: {input.InputType}");
        }

        // Display any validation errors.
        if (input.ValidationErrors is { Count: > 0 } errors)
        {
            foreach (var error in errors)
            {
                InteractionService.DisplayError(error);
            }
        }

        var result = inputType switch
        {
            InputType.Text => await InteractionService.PromptForStringAsync(
                promptText,
                binding: PromptBinding.CreateDefault(input.Value),
                required: input.Required,
                cancellationToken: cancellationToken),

            InputType.SecretText => await InteractionService.PromptForStringAsync(
                promptText,
                binding: PromptBinding.CreateDefault(input.Value),
                isSecret: true,
                required: input.Required,
                cancellationToken: cancellationToken),

            InputType.Choice => await HandleSelectInputAsync(input, promptText, cancellationToken),

            InputType.Boolean => (await InteractionService.PromptConfirmAsync(promptText, binding: PromptBinding.CreateDefault(ParseBooleanValue(input.Value)), cancellationToken: cancellationToken)).ToString().ToLowerInvariant(),

            InputType.Number => await HandleNumberInputAsync(input, promptText, cancellationToken),

            InputType.File => await HandleFileInputAsync(input, promptText, backchannel, interactionId, cancellationToken),

            _ => throw new InvalidOperationException($"Unsupported input type: {input.InputType}"),
        };

        return result;
    }

    private async Task<string?> HandleSelectInputAsync(PublishingPromptInput input, string promptText, CancellationToken cancellationToken)
    {
        if (input.Options is null || input.Options.Count == 0)
        {
            return await InteractionService.PromptForStringAsync(promptText, binding: PromptBinding.CreateDefault(input.Value), required: input.Required, cancellationToken: cancellationToken);
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
            cancellationToken: cancellationToken);

        if (value == CustomChoiceValue)
        {
            return await InteractionService.PromptForStringAsync(promptText, binding: PromptBinding.CreateDefault(input.Value), required: input.Required, cancellationToken: cancellationToken);
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
            binding: PromptBinding.CreateDefault(input.Value),
            validator: Validator,
            required: input.Required,
            cancellationToken: cancellationToken);
    }

    private async Task<string?> HandleFileInputAsync(PublishingPromptInput input, string promptText, IAppHostCliBackchannel backchannel, string interactionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(input.Name))
        {
            throw new InvalidOperationException("File prompt input is missing a name.");
        }
        var inputName = input.Name;

        ValidationResult Validator(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return ValidationResult.Success();
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(value);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return ValidationResult.Error("Please enter a valid file path.");
            }

            if (!File.Exists(fullPath))
            {
                return ValidationResult.Error("File does not exist.");
            }

            if (input.MaxFileSize is { } maxSize)
            {
                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Length > maxSize)
                {
                    return ValidationResult.Error($"'{Path.GetFileName(fullPath)}' exceeds the maximum size of {FormatHelpers.FormatFileSize(maxSize)}.");
                }
            }

            if (!string.IsNullOrEmpty(input.FileFilter))
            {
                var fileName = Path.GetFileName(fullPath);
                var filters = input.FileFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // Only validate against extension filters (e.g. ".pem", ".tar.gz"), skip MIME type patterns (e.g. "image/*").
                var extensionFilters = filters.Where(f => f.StartsWith('.'));
                if (extensionFilters.Any() && !extensionFilters.Any(f => fileName.EndsWith(f, StringComparison.OrdinalIgnoreCase)))
                {
                    return ValidationResult.Error($"'{Path.GetFileName(fullPath)}' does not match the accepted file types ({input.FileFilter}).");
                }
            }

            return ValidationResult.Success();
        }

        if (input.AllowMultipleFiles)
        {
            // Prompt for files repeatedly until the user provides an empty value.
            var filePaths = new List<string>();

            while (true)
            {
                var filePrompt = filePaths.Count == 0
                    ? promptText
                    : $"{promptText} (enter to finish)";

                var value = await InteractionService.PromptForFilePathAsync(
                    filePrompt,
                    validator: Validator,
                    required: filePaths.Count == 0 && input.Required,
                    cancellationToken: cancellationToken);

                if (string.IsNullOrWhiteSpace(value))
                {
                    break;
                }

                filePaths.Add(Path.GetFullPath(value));
            }

            return await UploadFilesAsync(filePaths, backchannel, interactionId, inputName, cancellationToken);
        }

        var singleValue = await InteractionService.PromptForFilePathAsync(
            promptText,
            binding: PromptBinding.CreateDefault(input.Value),
            validator: Validator,
            required: input.Required,
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(singleValue))
        {
            return string.Empty;
        }

        return await UploadFilesAsync([Path.GetFullPath(singleValue)], backchannel, interactionId, inputName, cancellationToken);
    }

    private static async Task<string> UploadFilesAsync(List<string> filePaths, IAppHostCliBackchannel backchannel, string interactionId, string inputName, CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0)
        {
            return string.Empty;
        }
        if (!int.TryParse(interactionId, CultureInfo.InvariantCulture, out var interactionIdValue))
        {
            throw new InvalidOperationException($"Prompt ID '{interactionId}' is not a valid interaction ID.");
        }

        var fileRefs = new List<FileReferenceDto>(filePaths.Count);

        foreach (var filePath in filePaths)
        {
            var fullPath = Path.GetFullPath(filePath);
            var fileName = Path.GetFileName(fullPath);

            // Upload the file to the AppHost and collect the reference.
            // Matching the same format the dashboard uses: [{"Id":"...","Name":"..."}]
            var uploadResponse = await backchannel.UploadFileAsync(fullPath, fileName, interactionIdValue, inputName, cancellationToken);
            fileRefs.Add(new FileReferenceDto { Id = uploadResponse.FileId, Name = fileName });
        }

        return JsonSerializer.Serialize(fileRefs.ToArray(), BackchannelJsonSerializerContext.Default.FileReferenceDtoArray);
    }

    private static bool ParseBooleanValue(string? value)
    {
        return bool.TryParse(value, out var result) && result;
    }

    private static (LogLevel Level, string Prefix) ParseLogLevel(string? logLevel) => logLevel?.ToUpperInvariant() switch
    {
        "TRACE" => (LogLevel.Trace, "TRC"),
        "DEBUG" => (LogLevel.Debug, "DBG"),
        "INFORMATION" or "INFO" => (LogLevel.Information, "INF"),
        "WARNING" or "WARN" => (LogLevel.Warning, "WRN"),
        "ERROR" => (LogLevel.Error, "ERR"),
        "CRITICAL" => (LogLevel.Critical, "CRT"),
        null or _ => (LogLevel.Information, "INF")
    };

    public sealed record PipelineParameterArguments(IReadOnlyDictionary<string, string?> Values)
    {
        public static PipelineParameterArguments Empty { get; } = new(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
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
        if (_suppressTerminalProgressBar)
        {
            return;
        }

        // Skip terminal progress bar in non-interactive environments
        if (!_hostEnvironment.SupportsInteractiveOutput)
        {
            return;
        }
        _terminalProgressBarStarted = true;
        Console.Write("\u001b]9;4;3\u001b\\");
    }

    /// <summary>
    /// Stops the terminal progress bar.
    /// </summary>
    private void StopTerminalProgressBar()
    {
        if (!_terminalProgressBarStarted)
        {
            return;
        }
        _terminalProgressBarStarted = false;
        Console.Write("\u001b]9;4;0\u001b\\");
    }
}
