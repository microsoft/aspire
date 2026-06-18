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
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal class ExecCommand : BaseCommand
{
    private readonly IDotNetCliRunner _runner;
    private readonly IInteractionService _interactionService;
    private readonly ICertificateService _certificateService;
    private readonly IProjectLocator _projectLocator;
    private readonly IAnsiConsole _ansiConsole;
    private readonly AspireCliTelemetry _telemetry;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IDotNetRuntimeSelector _runtimeSelector;

    public ExecCommand(
        IDotNetCliRunner runner,
        IInteractionService interactionService,
        ICertificateService certificateService,
        IProjectLocator projectLocator,
        IAnsiConsole ansiConsole,
        AspireCliTelemetry telemetry,
        IDotNetSdkInstaller sdkInstaller,
        IDotNetRuntimeSelector runtimeSelector,
        IFeatures features,
        ICliUpdateNotifier updateNotifier)
        : base("exec", ExecCommandStrings.Description, features, updateNotifier)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(interactionService);
        ArgumentNullException.ThrowIfNull(certificateService);
        ArgumentNullException.ThrowIfNull(projectLocator);
        ArgumentNullException.ThrowIfNull(ansiConsole);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(sdkInstaller);
        ArgumentNullException.ThrowIfNull(runtimeSelector);

        _runner = runner;
        _interactionService = interactionService;
        _certificateService = certificateService;
        _projectLocator = projectLocator;
        _ansiConsole = ansiConsole;
        _telemetry = telemetry;
        _sdkInstaller = sdkInstaller;
        _runtimeSelector = runtimeSelector;

        var projectOption = new Option<FileInfo?>("--project");
        projectOption.Description = ExecCommandStrings.ProjectArgumentDescription;
        Options.Add(projectOption);

        var resourceOption = new Option<string>("--resource", "-r");
        resourceOption.Description = ExecCommandStrings.TargetResourceArgumentDescription;
        Options.Add(resourceOption);

        var startResourceOption = new Option<string>("--start-resource", "-s");
        startResourceOption.Description = ExecCommandStrings.StartTargetResourceArgumentDescription;
        Options.Add(startResourceOption);

        var workdirOption = new Option<string>("--workdir", "-w");
        workdirOption.Description = ExecCommandStrings.WorkdirArgumentDescription;
        Options.Add(workdirOption);

        // only for --help output
        var commandOption = new Option<string>("--");
        commandOption.Description = ExecCommandStrings.CommandArgumentDescription;
        Options.Add(commandOption);

        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Check if the .NET SDK is available
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, _interactionService, _runtimeSelector, cancellationToken))
        {
            return ExitCodeConstants.SdkNotInstalled;
        }

        // validate required arguments firstly to fail fast if not found
        var targetResourceMode = "--resource";
        var targetResource = parseResult.GetValue<string>("--resource");
        if (string.IsNullOrEmpty(targetResource))
        {
            targetResourceMode = "--start-resource";
            targetResource = parseResult.GetValue<string>("--start-resource");
        }

        if (targetResource is null)
        {
            _interactionService.DisplayError(ExecCommandStrings.TargetResourceNotSpecified);
            return ExitCodeConstants.InvalidCommand;
        }

        // unmatched tokens are those which will be tried to parse as command.
        // if none - we should fail fast
        if (parseResult.UnmatchedTokens.Count == 0)
        {
            _interactionService.DisplayError(ExecCommandStrings.NoCommandSpecified);
            return ExitCodeConstants.InvalidCommand;
        }

        var (arbitraryFlags, commandTokens) = ParseCmdArgs(parseResult);

        if (commandTokens is null || commandTokens.Count == 0)
        {
            _interactionService.DisplayError(ExecCommandStrings.FailedToParseCommand);
            return ExitCodeConstants.InvalidCommand;
        }

        var buildOutputCollector = new OutputCollector();
        var runOutputCollector = new OutputCollector();

        IAppHostBackchannel? backchannel = null;
        Task<int>? pendingRun = null;
        int? commandExitCode = null;

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

            var waitForDebugger = parseResult.GetValue<bool>("--wait-for-debugger");
            if (waitForDebugger)
            {
                env[KnownConfigNames.WaitForDebugger] = "true";
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
                var backchannelCompletionSource = new TaskCompletionSource<IAppHostBackchannel>();
                pendingRun = _runner.RunAsync(
                    projectFile: effectiveAppHostProjectFile,
                    watch: false,
                    noBuild: false,
                    args: args,
                    env: env,
                    backchannelCompletionSource: backchannelCompletionSource,
                    options: runOptions,
                    cancellationToken: cancellationToken);

                // We wait for the back channel to be created to signal that
                // the AppHost is ready to accept requests.
                backchannel = await _interactionService.ShowStatusAsync(
                    $":linked_paperclips:  {RunCommandStrings.StartingAppHost}",
                    async () =>
                    {
                        // If we use the --wait-for-debugger option we print out the process ID
                        // of the apphost so that the user can attach to it.
                        if (waitForDebugger)
                        {
                            _interactionService.DisplayMessage(emoji: "bug", InteractionServiceStrings.WaitingForDebuggerToAttachToAppHost);
                        }

                        // The wait for the debugger in the apphost is done inside the CreateBuilder(...) method
                        // before the backchannel is created, therefore waiting on the backchannel is a
                        // good signal that the debugger was attached (or timed out).
                        var backchannel = await backchannelCompletionSource.Task.WaitAsync(cancellationToken);
                        return backchannel;
                    });

                commandExitCode = await _interactionService.ShowStatusAsync<int?>(
                    $":running_shoe: {ExecCommandStrings.Running}",
                    async () =>
                    {
                        // execute tool and stream the output
                        int? exitCode = null;
                        var outputStream = backchannel.ExecAsync(cancellationToken);
                        await foreach (var output in outputStream)
                        {
                            _interactionService.WriteConsoleLog(output.Text, output.LineNumber, output.Type, output.IsErrorMessage);
                            if (output.ExitCode is not null)
                            {
                                exitCode = output.ExitCode;
                            }
                        }

                        return exitCode;
                    });
            }
            finally
            {
                if (backchannel is not null)
                {
                    _ = await _interactionService.ShowStatusAsync<int>(
                    $":linked_paperclips: {ExecCommandStrings.StoppingAppHost}",
                    async () =>
                    {
                        await backchannel.RequestStopAsync(cancellationToken);
                        return ExitCodeConstants.Success;
                    });
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
                    _interactionService.DisplayLines(runOutputCollector.GetLines());
                    _interactionService.DisplayError(RunCommandStrings.ProjectCouldNotBeRun);
                    return result;
                }
                else
                {
                    return ExitCodeConstants.Success;
                }
            }
            else
            {
                _interactionService.DisplayLines(runOutputCollector.GetLines());
                _interactionService.DisplayError(RunCommandStrings.ProjectCouldNotBeRun);
                return ExitCodeConstants.FailedToDotnetRunAppHost;
            }
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
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
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.CertificateTrustError, ex.Message));
            return ExitCodeConstants.FailedToTrustCertificates;
        }
        catch (FailedToConnectBackchannelConnection ex)
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ErrorConnectingToAppHost, ex.Message));
            _interactionService.DisplayLines(runOutputCollector.GetLines());
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }
        catch (Exception ex)
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message));
            _interactionService.DisplayLines(runOutputCollector.GetLines());
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
