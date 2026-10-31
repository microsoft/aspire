// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Testing;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using StreamJsonRpc;

namespace Aspire.Cli.Commands;

internal sealed class TestCommand : BaseCommand
{
    private static readonly Option<bool> s_noBuildOption = new("--no-build") { Description = RunCommandStrings.NoBuildArgumentDescription };
    private static readonly Option<bool> s_detachOption = new("--detach") { Hidden = true };
    internal static readonly Option<OutputFormat> s_formatOption = new("--format") { Description = RunCommandStrings.TestFormatDescription };
    internal static readonly Option<bool> s_keepAliveOption = new("--keep-alive") { Description = RunCommandStrings.TestKeepAliveDescription };
    private readonly AppHostRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly CommonCommandServices _services;

    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;
    protected override TimeSpan GracefulShutdownBudget => AppHostRunner.s_gracefulShutdownBudget;

    public TestCommand(AppHostRunner runner, CommonCommandServices services, TimeProvider timeProvider)
        : base("test", RunCommandStrings.TestDescription, services)
    {
        _runner = runner;
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
            extensionDebug || _runner.IsDetachedStartChild())
        {
            var invalid = CommandResult.Failure(CliExitCodes.InvalidCommand, RunCommandStrings.TestIncompatibleOptions);
            WriteFailureJson(format, invalid);
            return invalid;
        }

        TestRunReporter? reporter = null;
        CommandResult? result = null;
        try
        {
            reporter = TestRunReporter.Create(ExecutionContext, InteractionService,
                _services.HostEnvironment.SupportsInteractiveOutput, format, _timeProvider,
                _services.LoggerFactory.CreateLogger<TestRunReporter>());
            var customization = new AppHostRunCustomization(
                new Dictionary<string, string> { [KnownConfigNames.TestOutputPath] = reporter.OutputPath },
                (session, ct) => ExecuteTestRunAsync(session, reporter, parseResult.GetValue(s_keepAliveOption), format, ct));
            result = await _runner.ExecuteAsync(parseResult, parseResult.GetValue(s_noBuildOption), customization, cancellationToken).ConfigureAwait(false);
            reporter.Complete(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = string.Format(CultureInfo.CurrentCulture, RunCommandStrings.TestArtifactFailure, ex.Message);
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

    private async Task<CommandResult> ExecuteTestRunAsync(AppHostRunSession session, TestRunReporter reporter,
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
            return CommandResult.Failure(CliExitCodes.AppHostIncompatible, RunCommandStrings.TestAppHostIncompatible);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = reporter.WaitForCompletionAsync(session.Backchannel, stop.Token);
        try
        {
            var finished = await Task.WhenAny(completion, session.RunTask).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (finished == session.RunTask && !completion.IsCompleted)
            {
                return CommandResult.Failure(CliExitCodes.TestRunFailed, RunCommandStrings.TestAppHostExitedEarly);
            }
            var passed = await completion.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (keepAlive)
            {
                reporter.ReportCompletedResources(passed);
                InteractionService.DisplaySubtleMessage(RunCommandStrings.TestKeepAliveMessage);
                await WaitForInspectionAsync(session, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await session.Backchannel.RequestStopAsync(cancellationToken)
                    .WaitAsync(GracefulShutdownBudget, _timeProvider, cancellationToken).ConfigureAwait(false);
                await session.RunTask.WaitAsync(GracefulShutdownBudget, _timeProvider, cancellationToken).ConfigureAwait(false);
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
                    string.Format(CultureInfo.CurrentCulture, RunCommandStrings.AppHostExitedWithCode, exitCode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Cancelled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandResult.Failure(CliExitCodes.TestRunFailed,
                string.Format(CultureInfo.CurrentCulture, RunCommandStrings.TestArtifactFailure, ex.Message));
        }
        finally
        {
            stop.Cancel();
            ObserveFaults(completion);
        }
    }

    private async Task WaitForInspectionAsync(AppHostRunSession session, CancellationToken cancellationToken)
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
                    throw new ConnectionLostException(RunCommandStrings.TestKeepAliveDisconnected);
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
        console.DisplaySubtleMessage($"{RunCommandStrings.AppHost.EscapeMarkup()}: {appHostRelativePath.EscapeMarkup()}", allowMarkup: true);
        if (!isExtensionHost)
        {
            console.DisplaySubtleMessage($"{RunCommandStrings.Dashboard.EscapeMarkup()}: " +
                (string.IsNullOrEmpty(dashboardUrl) ? "N/A" : MarkupHelpers.SafeLink(console, dashboardUrl)), allowMarkup: true);
            if (!string.IsNullOrEmpty(codespacesUrl))
            {
                console.DisplaySubtleMessage($"{RunCommandStrings.Dashboard.EscapeMarkup()}: {MarkupHelpers.SafeLink(console, codespacesUrl)}", allowMarkup: true);
            }
        }
        console.DisplaySubtleMessage($"{RunCommandStrings.Logs.EscapeMarkup()}: {MarkupHelpers.SafeFileLink(console, logFilePath)}", allowMarkup: true);
    }
}
