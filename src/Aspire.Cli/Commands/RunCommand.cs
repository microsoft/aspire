// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

internal sealed class RunCommand : BaseCommand
{
    private readonly AppHostRunner _runner;
    public RunCommand(AppHostRunner runner, CommonCommandServices services)
        : base("run", RunCommandStrings.Description, services)
    {
        _runner = runner;
        AddRunOptions(this);
        TreatUnmatchedTokensAsErrors = false;
    }
    internal static readonly Option<bool> s_detachOption = new("--detach")
    {
        Description = RunCommandStrings.DetachArgumentDescription
    };
    private static readonly Option<bool> s_noBuildOption = new("--no-build")
    {
        Description = RunCommandStrings.NoBuildArgumentDescription
    };
    internal static readonly TimeSpan s_gracefulShutdownBudget = AppHostRunner.s_gracefulShutdownBudget;
    private bool _isDetachMode;

    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;
    protected override TimeSpan GracefulShutdownBudget => s_gracefulShutdownBudget;
    protected override bool UpdateNotificationsEnabled => !_isDetachMode;

    internal override void PrepareForExecution(ParseResult parseResult)
    {
        _isDetachMode = parseResult.GetValue(s_detachOption) || _runner.IsDetachedStartChild();
    }

    internal static void AddRunOptions(Command command)
    {
        command.Options.Add(s_detachOption);
        command.Options.Add(s_noBuildOption);
        AppHostLauncher.AddLaunchOptions(command);
    }

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) =>
        _runner.ExecuteAsync(parseResult, parseResult.GetValue(s_noBuildOption), customization: null, cancellationToken);

    public void ProcessResourceState(RpcResourceState resourceState, Action<string, string> endpointWriter) =>
        _runner.ProcessResourceState(resourceState, endpointWriter);

    internal static int RenderAppHostSummary(IInteractionService console, string appHostRelativePath,
        string? dashboardUrl, string? codespacesUrl, string logFilePath, bool isExtensionHost, int? pid = null) =>
        AppHostRunner.RenderAppHostSummary(console, appHostRelativePath, dashboardUrl, codespacesUrl, logFilePath, isExtensionHost, pid);

    internal static Task CaptureAppHostLogsAsync(FileLoggerProvider fileLoggerProvider,
        IAppHostCliBackchannel backchannel, IInteractionService interactionService, CancellationToken cancellationToken) =>
        AppHostRunner.CaptureAppHostLogsAsync(fileLoggerProvider, backchannel, interactionService, cancellationToken);
}
