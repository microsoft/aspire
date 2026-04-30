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

internal sealed class DestroyCommand : PipelineCommandBase
{
    internal override HelpGroup HelpGroup => HelpGroup.Deployment;

    private readonly Option<bool> _yesOption;

    public DestroyCommand(IDotNetCliRunner runner, IInteractionService interactionService, IProjectLocator projectLocator, AspireCliTelemetry telemetry, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory, IConfiguration configuration, ILogger<DestroyCommand> logger, IAnsiConsole ansiConsole)
        : base("destroy", DestroyCommandStrings.Description, runner, interactionService, projectLocator, telemetry, features, updateNotifier, executionContext, hostEnvironment, projectFactory, configuration, logger, ansiConsole)
    {
        _yesOption = new Option<bool>("--yes", "-y")
        {
            Description = DestroyCommandStrings.YesOptionDescription
        };
        Options.Add(_yesOption);
    }

    protected override string OperationCompletedPrefix => DestroyCommandStrings.OperationCompletedPrefix;
    protected override string OperationFailedPrefix => DestroyCommandStrings.OperationFailedPrefix;
    protected override string GetOutputPathDescription() => DestroyCommandStrings.OutputPathArgumentDescription;

    protected override Task<string[]> GetRunArgumentsAsync(string? fullyQualifiedOutputPath, string[] unmatchedTokens, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var baseArgs = new List<string> { "--operation", "publish", "--step", "destroy" };

        if (fullyQualifiedOutputPath != null)
        {
            baseArgs.AddRange(["--output-path", fullyQualifiedOutputPath]);
        }

        var yes = parseResult.GetValue(_yesOption);
        if (yes)
        {
            baseArgs.AddRange(["--yes", "true"]);
        }

        var logLevel = parseResult.GetValue(s_pipelineLogLevelOption);
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

    protected override string GetCanceledMessage() => DestroyCommandStrings.DestroyCanceled;

    protected override string GetProgressMessage(ParseResult parseResult)
    {
        return "Executing step destroy";
    }
}
