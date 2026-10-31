// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

internal sealed class TestCommand : BaseCommand
{
    private readonly RunCommand _runCommand;

    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    protected override TimeSpan GracefulShutdownBudget => RunCommand.s_gracefulShutdownBudget;

    public TestCommand(RunCommand runCommand, CommonCommandServices services)
        : base("test", RunCommandStrings.TestDescription, services)
    {
        _runCommand = runCommand;
        RunCommand.AddRunOptions(this);
        TreatUnmatchedTokensAsErrors = false;
    }

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) =>
        _runCommand.ExecuteRunAsync(parseResult, testMode: true, cancellationToken);
}
