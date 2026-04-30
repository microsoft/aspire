// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal sealed class StartCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly AppHostLauncher _appHostLauncher;

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
        AppHostLauncher appHostLauncher)
        : base("start", StartCommandStrings.Description,
               features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _appHostLauncher = appHostLauncher;

        Options.Add(s_noBuildOption);
        AppHostLauncher.AddLaunchOptions(this);

        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var passedAppHostProjectFile = parseResult.GetValue(AppHostLauncher.s_appHostOption);
        var format = parseResult.GetValue(AppHostLauncher.s_formatOption);
        var isolated = parseResult.GetValue(AppHostLauncher.s_isolatedOption);

        var noBuild = parseResult.GetValue(s_noBuildOption);
        // `aspire start` is always user-initiated — the VS Code extension only invokes
        // `aspire run`, never `aspire start`. So we hardcode isExtensionHost to false
        // to ensure dashboard URLs always appear in the summary output.
        var isExtensionHost = false;
        var waitForDebugger = parseResult.GetValue(RootCommand.WaitForDebuggerOption);
        var globalArgs = RootCommand.GetChildProcessArgs(parseResult);
        var appHostArguments = AppHostLauncher.GetAppHostArguments(parseResult);
        var additionalArgs = new List<string>();

        if (noBuild)
        {
            additionalArgs.Add("--no-build");
        }

        AppHostLauncher.AddAppHostArguments(additionalArgs, appHostArguments);

        return await _appHostLauncher.LaunchDetachedAsync(
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
