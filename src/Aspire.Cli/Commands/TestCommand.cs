// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Resources;
using Aspire.Cli.Testing;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal sealed class TestCommand : BaseCommand
{
    private readonly RunCommand _runCommand;
    private readonly TimeProvider _timeProvider;
    private readonly CommonCommandServices _services;

    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    protected override TimeSpan GracefulShutdownBudget => RunCommand.s_gracefulShutdownBudget;

    public TestCommand(RunCommand runCommand, CommonCommandServices services, TimeProvider timeProvider)
        : base("test", RunCommandStrings.TestDescription, services)
    {
        _runCommand = runCommand;
        _services = services;
        _timeProvider = timeProvider;
        RunCommand.AddRunOptions(this);
        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        CommandResult? result = null;
        try
        {
            var reporter = TestRunReporter.Create(ExecutionContext, InteractionService,
                _services.HostEnvironment.SupportsInteractiveOutput, _timeProvider,
                _services.LoggerFactory.CreateLogger<TestRunReporter>());
            result = await _runCommand.ExecuteRunAsync(parseResult, reporter, cancellationToken).ConfigureAwait(false);
            reporter.Complete(result);

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = string.Format(System.Globalization.CultureInfo.CurrentCulture, RunCommandStrings.TestArtifactFailure, ex.Message);
            if (result is { ExitCode: not CliExitCodes.Success })
            {
                InteractionService.DisplayError(error);
                return result;
            }

            return CommandResult.Failure(CliExitCodes.TestRunFailed, error);
        }
    }
}
