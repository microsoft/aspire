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

namespace Aspire.Cli.Commands;

internal class ExecCommand : BaseCommand
{
    private readonly IDotNetCliRunner _runner;
    private readonly ICertificateService _certificateService;
    private readonly IProjectLocator _projectLocator;
    private readonly IDotNetSdkInstaller _sdkInstaller;

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", ExecCommandStrings.ProjectArgumentDescription);
    private static readonly Option<string> s_resourceOption = new("--resource", "-r")
    {
        Description = ExecCommandStrings.TargetResourceArgumentDescription
    };
    private static readonly Option<string> s_startResourceOption = new("--start-resource", "-s")
    {
        Description = ExecCommandStrings.StartTargetResourceArgumentDescription
    };
    private static readonly Option<string> s_workdirOption = new("--workdir", "-w")
    {
        Description = ExecCommandStrings.WorkdirArgumentDescription
    };
    private static readonly Option<string> s_commandOption = new("--")
    {
        Description = ExecCommandStrings.CommandArgumentDescription
    };

    public ExecCommand(
        IDotNetCliRunner runner,
        IInteractionService interactionService,
        ICertificateService certificateService,
        IProjectLocator projectLocator,
        AspireCliTelemetry telemetry,
        IDotNetSdkInstaller sdkInstaller,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext)
        : base("exec", ExecCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _runner = runner;
        _certificateService = certificateService;
        _projectLocator = projectLocator;
        _sdkInstaller = sdkInstaller;

        Options.Add(s_appHostOption);
        Options.Add(s_resourceOption);
        Options.Add(s_startResourceOption);
        Options.Add(s_workdirOption);
        // only for --help output
        Options.Add(s_commandOption);

        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Check if the .NET SDK is available
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, InteractionService, Telemetry, cancellationToken))
        {
            return ExitCodeConstants.SdkNotInstalled;
        }

        // validate required arguments firstly to fail fast if not found
        var targetResourceMode = "--resource";
        var targetResource = parseResult.GetValue(s_resourceOption);
        if (string.IsNullOrEmpty(targetResource))
        {
            targetResourceMode = "--start-resource";
            targetResource = parseResult.GetValue(s_startResourceOption);
        }

        if (targetResource is null)
        {
            InteractionService.DisplayError(ExecCommandStrings.TargetResourceNotSpecified);
            return ExitCodeConstants.InvalidCommand;
        }

        // unmatched tokens are those which will be tried to parse as command.
        // if none - we should fail fast
        if (parseResult.UnmatchedTokens.Count == 0)
        {
            InteractionService.DisplayError(ExecCommandStrings.NoCommandSpecified);
            return ExitCodeConstants.InvalidCommand;
        }

        var (arbitraryFlags, commandTokens) = ParseCmdArgs(parseResult);

        if (commandTokens is null || commandTokens.Count == 0)
        {
            InteractionService.DisplayError(ExecCommandStrings.FailedToParseCommand);
            return ExitCodeConstants.InvalidCommand;
        }

        var buildOutputCollector = new OutputCollector();
        var runOutputCollector = new OutputCollector();

        IAppHostCliBackchannel? backchannel = null;
        Task<int>? pendingRun = null;
        int? commandExitCode = null;

        (bool IsCompatibleAppHost, bool SupportsBackchannel, string? AspireHostingVersion)? appHostCompatibilityCheck = null;
        try
        {
            using var activity = Telemetry.StartDiagnosticActivity(this.Name);

            var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
            var effectiveAppHostProjectFile = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, createSettingsFile: true, cancellationToken);

            if (effectiveAppHostProjectFile is null)
            {
                return ExitCodeConstants.FailedToFindProject;
            }

            if (string.Equals(effectiveAppHostProjectFile.Extension, ".cs", StringComparison.OrdinalIgnoreCase))
            {
                InteractionService.DisplayError(ErrorStrings.CommandNotSupportedWithSingleFileAppHost);
                return ExitCodeConstants.SingleFileAppHostNotSupported;
            }

            var env = new Dictionary<string, string>();

            var waitForDebugger = parseResult.GetValue(RootCommand.WaitForDebuggerOption);
            if (waitForDebugger)
            {
                env[KnownConfigNames.WaitForDebugger] = "true";
            }

            appHostCompatibilityCheck = await AppHostHelper.CheckAppHostCompatibilityAsync(_runner, InteractionService, effectiveAppHostProjectFile, Telemetry, ExecutionContext.WorkingDirectory, ExecutionContext.LogFilePath, cancellationToken);
            if (!appHostCompatibilityCheck?.IsCompatibleAppHost ?? throw new InvalidOperationException(RunCommandStrings.IsCompatibleAppHostIsNull))
            {
                return ExitCodeConstants.FailedToDotnetRunAppHost;
            }

            var runOptions = new DotNetCliRunnerInvocationOptions
            {
                StandardOutputCallback = runOutputCollector.AppendOutput,
                StandardErrorCallback = runOutputCollector.AppendError,
            };

            string[] args = [
                "--operation", "run",
                targetResourceMode, targetResource!,

                // a bit hacky, but in order to pass a full command with possible quotes and etc properly without losing the signature
                // we can wrap it in a string and pass it as a single argument
                "--command", $"\"{string.Join(" ", commandTokens)}\"",

                ..arbitraryFlags,
            ];

            try
            {
                var backchannelCompletionSource = new TaskCompletionSource<IAppHostCliBackchannel>();
                pendingRun = _runner.RunAsync(
                    projectFile: effectiveAppHostProjectFile,
                    watch: false,
                    noBuild: false,
                    noRestore: false,
                    args: args,
                    env: env,
                    backchannelCompletionSource: backchannelCompletionSource,
                    options: runOptions,
                    cancellationToken: cancellationToken);

                // We wait for the back channel to be created to signal that
                // the AppHost is ready to accept requests.
                backchannel = await InteractionService.ShowStatusAsync(
                    RunCommandStrings.StartingAppHost,
                    async () =>
                    {
                        // If we use the --wait-for-debugger option we print out the process ID
                        // of the apphost so that the user can attach to it.
                        if (waitForDebugger)
                        {
                            InteractionService.DisplayMessage(KnownEmojis.Bug, InteractionServiceStrings.WaitingForDebuggerToAttachToAppHost);
                        }

                        // The wait for the debugger in the apphost is done inside the CreateBuilder(...) method
                        // before the backchannel is created, therefore waiting on the backchannel is a
                        // good signal that the debugger was attached (or timed out).
                        var backchannel = await backchannelCompletionSource.Task.WaitAsync(cancellationToken);
                        return backchannel;
                    }, emoji: KnownEmojis.LinkedPaperclips);

                commandExitCode = await InteractionService.ShowStatusAsync<int?>(
                    ExecCommandStrings.Running,
                    async () =>
                    {
                        // execute tool and stream the output
                        int? exitCode = null;
                        var outputStream = backchannel.ExecAsync(cancellationToken);
                        await foreach (var output in outputStream)
                        {
                            InteractionService.WriteConsoleLog(output.Text, output.LineNumber, output.Type, output.IsErrorMessage);
                            if (output.ExitCode is not null)
                            {
                                exitCode = output.ExitCode;
                            }
                        }

                        return exitCode;
                    }, emoji: KnownEmojis.RunningShoe);
            }
            finally
            {
                if (backchannel is not null)
                {
                    _ = await InteractionService.ShowStatusAsync<int>(
                    ExecCommandStrings.StoppingAppHost,
                    async () =>
                    {
                        await backchannel.RequestStopAsync(cancellationToken);
                        return ExitCodeConstants.Success;
                    }, emoji: KnownEmojis.LinkedPaperclips);
                }
            }

            if (commandExitCode is not null)
            {
                // if there is a deterministic output of the command with exit code - we should display that
                return commandExitCode.Value;
            }

            if (pendingRun is not null)
            {
                var result = await pendingRun;
                if (result != 0)
                {
                    InteractionService.DisplayLines(runOutputCollector.GetLines());
                    InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, RunCommandStrings.ProjectCouldNotBeRun, ExecutionContext.LogFilePath));
                    return result;
                }
                else
                {
                    return ExitCodeConstants.Success;
                }
            }
            else
            {
                InteractionService.DisplayLines(runOutputCollector.GetLines());
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, RunCommandStrings.ProjectCouldNotBeRun, ExecutionContext.LogFilePath));
                return ExitCodeConstants.FailedToDotnetRunAppHost;
            }
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
        {
            InteractionService.DisplayCancellationMessage();
            return ExitCodeConstants.Success;
        }
        catch (ProjectLocatorException ex)
        {
            return HandleProjectLocatorException(ex, InteractionService, Telemetry);
        }
        catch (AppHostIncompatibleException ex)
        {
            Telemetry.RecordError(ex.Message, ex);
            return InteractionService.DisplayIncompatibleVersionError(
                ex,
                appHostCompatibilityCheck?.AspireHostingVersion ?? throw new InvalidOperationException(ErrorStrings.AspireHostingVersionNull)
                );
        }
        catch (CertificateServiceException ex)
        {
            var errorMessage = string.Format(CultureInfo.CurrentCulture, TemplatingStrings.CertificateTrustError, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            InteractionService.DisplayError(errorMessage);
            return ExitCodeConstants.FailedToTrustCertificates;
        }
        catch (FailedToConnectBackchannelConnection ex)
        {
            var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ErrorConnectingToAppHost, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            InteractionService.DisplayError(errorMessage);
            InteractionService.DisplayLines(runOutputCollector.GetLines());
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }
        catch (Exception ex)
        {
            var errorMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            InteractionService.DisplayError(errorMessage);
            InteractionService.DisplayLines(runOutputCollector.GetLines());
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }
    }

    private (ICollection<string> arbitary, ICollection<string> command) ParseCmdArgs(ParseResult parseResult)
    {
        var allTokens = parseResult.UnmatchedTokens.ToList();
        int delimiterIndex = allTokens.IndexOf("--");
        List<string> arbitraryFlags = new();
        List<string> commandTokens = new();

        // Find the index of the first token that is not an option (doesn't start with '-') and is not the value for a known option
        // We'll use the options defined in this command to skip known option values
        var knownOptions = new HashSet<string>(Options.SelectMany(o => o.Aliases));
        int i = 0;
        while (i < allTokens.Count)
        {
            if (delimiterIndex >= 0 && i == delimiterIndex)
            {
                // Everything after -- is command
                commandTokens.AddRange(allTokens.Skip(i + 1));
                break;
            }

            var token = allTokens[i];
            if (knownOptions.Contains(token))
            {
                // Skip the option and its value (if it has one)
                var option = Options.FirstOrDefault(o => o.Aliases.Contains(token));
                if (option is not null)
                {
                    // If the option is not a bool, it expects a value
                    var isFlag = option.Arity.MaximumNumberOfValues == 0;
                    if (!isFlag && i + 1 < allTokens.Count)
                    {
                        i += 2;
                        continue;
                    }
                }
                i++;
                continue;
            }
            else if (token.StartsWith("-"))
            {
                // Arbitrary flag
                arbitraryFlags.Add(token);
                i++;
                continue;
            }
            else
            {
                // First non-option, non-flag token is the start of the command (if not using --)
                commandTokens.AddRange(allTokens.Skip(i));
                break;
            }
        }

        return (arbitraryFlags, commandTokens);
    }
}
