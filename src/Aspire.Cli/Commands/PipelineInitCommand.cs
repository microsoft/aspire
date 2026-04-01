// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class PipelineInitCommand : PipelineCommandBase
{
    internal override HelpGroup HelpGroup => HelpGroup.Deployment;

    public PipelineInitCommand(IDotNetCliRunner runner, IInteractionService interactionService, IProjectLocator projectLocator, AspireCliTelemetry telemetry, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory, IConfiguration configuration, ILogger<PipelineInitCommand> logger, IAnsiConsole ansiConsole)
        : base("init", PipelineInitCommandStrings.Description, runner, interactionService, projectLocator, telemetry, features, updateNotifier, executionContext, hostEnvironment, projectFactory, configuration, logger, ansiConsole)
    {
    }

    protected override string OperationCompletedPrefix => PipelineInitCommandStrings.OperationCompletedPrefix;
    protected override string OperationFailedPrefix => PipelineInitCommandStrings.OperationFailedPrefix;
    protected override string GetOutputPathDescription() => PipelineInitCommandStrings.OutputPathArgumentDescription;

    protected override Task<string[]> GetRunArgumentsAsync(string? fullyQualifiedOutputPath, string[] unmatchedTokens, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var baseArgs = new List<string> { "--operation", "publish", "--step", "pipeline-init" };

        if (fullyQualifiedOutputPath is not null)
        {
            baseArgs.AddRange(["--output-path", fullyQualifiedOutputPath]);
        }

        var logLevel = parseResult.GetValue(s_logLevelOption);
        if (!string.IsNullOrEmpty(logLevel))
        {
            baseArgs.AddRange(["--log-level", logLevel!]);
        }

        var includeExceptionDetails = parseResult.GetValue(s_includeExceptionDetailsOption);
        if (includeExceptionDetails)
        {
            baseArgs.AddRange(["--include-exception-details", "true"]);
        }

        var environment = parseResult.GetValue(s_environmentOption);
        if (!string.IsNullOrEmpty(environment))
        {
            baseArgs.AddRange(["--environment", environment!]);
        }

        baseArgs.AddRange(unmatchedTokens);

        return Task.FromResult<string[]>([.. baseArgs]);
    }

    protected override string GetCanceledMessage() => PipelineInitCommandStrings.OperationCanceled;

    protected override string GetProgressMessage(ParseResult parseResult)
    {
        return "Generating pipeline workflow files";
    }
}
