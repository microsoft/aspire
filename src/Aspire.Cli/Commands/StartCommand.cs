// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Commands;

internal sealed class StartCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

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
        var additionalArgs = parseResult.UnmatchedTokens.ToList();

        if (noBuild)
        {
            additionalArgs.Add("--no-build");
        }

        if (!AppHostStartupTimeout.TryGetTimeoutSeconds(_configuration, InteractionService, out var timeoutSeconds))
        {
            return ExitCodeConstants.InvalidCommand;
        }

        return await _appHostLauncher.LaunchDetachedAsync(
            passedAppHostProjectFile,
            format,
            isolated,
            isExtensionHost,
            waitForDebugger,
            timeoutSeconds,
            globalArgs,
            additionalArgs,
            cancellationToken);
    }
}
