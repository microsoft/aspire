// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Profiling;
using Aspire.Cli.Resources;
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
    private readonly ProfileCaptureState _profileCaptureState;

    private static readonly Option<bool> s_noBuildOption = new("--no-build")
    {
        Description = RunCommandStrings.NoBuildArgumentDescription
    };

    public StartCommand(
        AppHostLauncher appHostLauncher,
        IConfiguration configuration,
        ProfileCaptureState profileCaptureState,
        CommonCommandServices services)
        : base("start", StartCommandStrings.Description,
               services)
    {
        _appHostLauncher = appHostLauncher;
        _configuration = configuration;
        _profileCaptureState = profileCaptureState;

        Options.Add(s_noBuildOption);
        AppHostLauncher.AddLaunchOptions(this);

        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var passedAppHostProjectFile = parseResult.GetValue(AppHostLauncher.s_appHostOption);
        var format = parseResult.GetValue(AppHostLauncher.s_formatOption);
        var explicitIsolated = AppHostLauncher.GetExplicitIsolated(parseResult);
        var launchProfile = parseResult.GetValue(AppHostLauncher.s_launchProfileOption);

        var noBuild = parseResult.GetValue(s_noBuildOption);
        // The detached start path is always user-initiated. When invoked from the
        // Aspire terminal, it is delegated to VS Code below before reaching this
        // point, so keep detached summary output visible.
        var isExtensionHost = false;
        var waitForDebugger = parseResult.GetValue(RootCommand.WaitForDebuggerOption);
        var globalArgs = RootCommand.GetChildProcessArgs(parseResult);
        var appHostArgs = parseResult.UnmatchedTokens;
        var additionalArgs = new List<string>();
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
            var debugSessionArguments = ParseResultHelper.GetForwardedArguments(
                parseResult,
                AppHostLauncher.s_appHostOption.InnerOption,
                AppHostLauncher.s_appHostOption.LegacyOption,
                AppHostLauncher.s_formatOption,
                RootCommand.StartDebugSessionOption,
                RootCommand.NonInteractiveOption);
            extensionInteractionService.DisplayConsolePlainText(string.Format(CultureInfo.CurrentCulture, startDebugSession ? RunCommandStrings.StartingDebugSessionInExtension : RunCommandStrings.StartingRunSessionInExtension, "start"));
            await extensionInteractionService.StartDebugSessionAsync(
                ExecutionContext.WorkingDirectory.FullName,
                passedAppHostProjectFile?.FullName,
                debug: startDebugSession,
                new DebugSessionOptions
                {
                    Command = "run",
                    Args = [.. debugSessionArguments.Tokens],
                    AppHostSelectionOrigin = passedAppHostProjectFile is not null
                        ? DebugSessionOptions.ExplicitCliAppHostSelectionOrigin
                        : DebugSessionOptions.DefaultDiscoveryAppHostSelectionOrigin
                });
            _profileCaptureState.MarkTransferred();

            return CommandResult.Success();
        }

        if (noBuild)
        {
            additionalArgs.Add("--no-build");
        }

        if (!string.IsNullOrEmpty(launchProfile))
        {
            additionalArgs.Add($"{AppHostLauncher.s_launchProfileOption.Name}={launchProfile}");
        }

        if (appHostArgs.Count > 0)
        {
            additionalArgs.Add("--");
            additionalArgs.AddRange(appHostArgs);
        }

        if (!AppHostStartupTimeout.TryGetTimeoutSeconds(_configuration, InteractionService, out var timeoutSeconds))
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        return await _appHostLauncher.LaunchDetachedAsync(
            passedAppHostProjectFile,
            format,
            explicitIsolated,
            launchProfile,
            isExtensionHost,
            waitForDebugger,
            timeoutSeconds,
            globalArgs,
            additionalArgs,
            stopAfterLaunchDelay,
            cancellationToken);
    }
}
