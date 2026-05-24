// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Commands;

internal sealed class StartCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    protected override bool UpdateNotificationsEnabled => true;

    private readonly AppHostLauncher _appHostLauncher;
    private readonly IConfiguration _configuration;

    private static readonly Option<bool> s_noBuildOption = new("--no-build")
    {
        Description = RunCommandStrings.NoBuildArgumentDescription
    };

    public StartCommand(
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        AppHostLauncher appHostLauncher,
        IConfiguration configuration)
        : base("start", StartCommandStrings.Description,
               features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _appHostLauncher = appHostLauncher;
        _configuration = configuration;

        Options.Add(s_noBuildOption);
        AppHostLauncher.AddLaunchOptions(this);

        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var passedAppHostProjectFile = parseResult.GetValue(AppHostLauncher.s_appHostOption);
        var format = parseResult.GetValue(AppHostLauncher.s_formatOption);
        var isolated = parseResult.GetValue(AppHostLauncher.s_isolatedOption);

        var noBuild = parseResult.GetValue(s_noBuildOption);
        // The detached start path is always user-initiated. When invoked from the
        // Aspire terminal, it is delegated to VS Code below before reaching this
        // point, so keep detached summary output visible.
        var isExtensionHost = false;
        var waitForDebugger = parseResult.GetValue(RootCommand.WaitForDebuggerOption);
        var globalArgs = RootCommand.GetChildProcessArgs(parseResult);
        var additionalArgs = parseResult.UnmatchedTokens.ToList();
        var captureProfile = parseResult.GetValue(RootCommand.CaptureProfileOption);
        var stopAfterLaunchDelay = captureProfile
            ? TimeSpan.FromSeconds(parseResult.GetValue(RootCommand.CaptureProfileDelayOption))
            : (TimeSpan?)null;

        // When running in an extension host without an active debug session, delegate
        // to VS Code to start an interactive run session (non-debug) instead of launching
        // the AppHost detached. This preserves interactive console behavior and allows
        // the extension to manage the AppHost lifecycle.
        var nonInteractive = parseResult.GetValue(RootCommand.NonInteractiveOption);
        if (!nonInteractive
            && format != OutputFormat.Json
            && ExtensionHelper.IsExtensionHost(InteractionService, out var extensionInteractionService, out _)
            && string.IsNullOrEmpty(_configuration[KnownConfigNames.ExtensionDebugSessionId]))
        {
            var startDebugSession = parseResult.GetValue(RootCommand.StartDebugSessionOption);
            var debugSessionArgs = new List<string>();
            if (isolated)
            {
                debugSessionArgs.Add("--isolated");
            }

            if (noBuild)
            {
                debugSessionArgs.Add("--no-build");
            }

            debugSessionArgs.AddRange(globalArgs);

            if (captureProfile)
            {
                debugSessionArgs.Add("--capture-profile");

                if (parseResult.GetValue(RootCommand.CaptureProfileOutputOption) is { } captureProfileOutput)
                {
                    debugSessionArgs.Add("--capture-profile-output");
                    debugSessionArgs.Add(captureProfileOutput.FullName);
                }

                if (parseResult.GetResult(RootCommand.CaptureProfileDelayOption) is { Implicit: false })
                {
                    debugSessionArgs.Add("--capture-profile-delay");
                    debugSessionArgs.Add(parseResult.GetValue(RootCommand.CaptureProfileDelayOption).ToString(CultureInfo.InvariantCulture));
                }
            }

            if (additionalArgs.Count > 0)
            {
                debugSessionArgs.Add("--");
                debugSessionArgs.AddRange(additionalArgs);
            }

            extensionInteractionService.DisplayConsolePlainText(string.Format(CultureInfo.CurrentCulture, startDebugSession ? RunCommandStrings.StartingDebugSessionInExtension : RunCommandStrings.StartingRunSessionInExtension, "start"));
            await extensionInteractionService.StartDebugSessionAsync(
                ExecutionContext.WorkingDirectory.FullName,
                passedAppHostProjectFile?.FullName,
                debug: startDebugSession,
                new DebugSessionOptions
                {
                    Command = "run",
                    Args = debugSessionArgs.Count > 0 ? [.. debugSessionArgs] : null
                });

            return CommandResult.Success();
        }

        if (noBuild)
        {
            additionalArgs.Add("--no-build");
        }

        return await _appHostLauncher.LaunchDetachedAsync(
            passedAppHostProjectFile,
            format,
            isolated,
            isExtensionHost,
            waitForDebugger,
            globalArgs,
            additionalArgs,
            stopAfterLaunchDelay,
            cancellationToken);
    }
}
