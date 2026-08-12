// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class DeployCommand : PipelineCommandBase
{
    internal override HelpGroup HelpGroup => HelpGroup.Deployment;

    private readonly Option<bool> _clearCacheOption;

    public DeployCommand(IDotNetCliRunner runner, IProjectLocator projectLocator, IFeatures features, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory, IConfiguration configuration, ILogger<DeployCommand> logger, IAnsiConsole ansiConsole,
        CommonCommandServices services)
        : base("deploy", DeployCommandStrings.Description, runner, projectLocator, features, hostEnvironment, projectFactory, configuration, logger, ansiConsole, services)
    {
        _clearCacheOption = new Option<bool>("--clear-cache")
        {
            Description = DeployCommandStrings.ClearCacheOptionDescription
        };
        Options.Add(_clearCacheOption);
    }

    protected override string OperationCompletedPrefix => DeployCommandStrings.OperationCompletedPrefix;
    protected override string OperationFailedPrefix => DeployCommandStrings.OperationFailedPrefix;
    protected override string GetOutputPathDescription() => DeployCommandStrings.OutputPathArgumentDescription;

    protected override Task<string[]> GetRunArgumentsAsync(string? fullyQualifiedOutputPath, string[] unmatchedTokens, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var baseArgs = new List<string> { "--operation", "publish", "--step", "deploy" };

        if (fullyQualifiedOutputPath != null)
        {
            baseArgs.AddRange(["--output-path", fullyQualifiedOutputPath]);
        }

        var clearCache = parseResult.GetValue(_clearCacheOption);
        if (clearCache)
        {
            baseArgs.AddRange(["--clear-cache", "true"]);
        }

        // Add --log-level and --envionment flags if specified
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

    protected override string GetCanceledMessage() => DeployCommandStrings.DeploymentCanceled;

    protected override string? GetTargetStepName(ParseResult parseResult) => "deploy";

    protected override string GetProgressMessage(ParseResult parseResult)
    {
        return "Executing step deploy";
    }
}
