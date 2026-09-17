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

internal sealed class DoCommand : PipelineCommandBase
{
    internal override HelpGroup HelpGroup => HelpGroup.Deployment;

    private readonly Argument<string> _stepArgument;

    public DoCommand(IDotNetCliRunner runner, IProjectLocator projectLocator, IFeatures features, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory, IConfiguration configuration, ILogger<DoCommand> logger, IAnsiConsole ansiConsole,
        CommonCommandServices services)
        : base("do", DoCommandStrings.Description, runner, projectLocator, features, hostEnvironment, projectFactory, configuration, logger, ansiConsole, services)
    {
        _stepArgument = new Argument<string>("step")
        {
            Description = DoCommandStrings.StepArgumentDescription,
            Arity = ArgumentArity.ZeroOrOne
        };
        Arguments.Add(_stepArgument);

        Validators.Add(result =>
        {
            var step = result.GetValue(_stepArgument);
            var listSteps = result.GetValue(s_listStepsOption);
            if (!string.IsNullOrEmpty(step))
            {
                return;
            }

            if (listSteps)
            {
                return;
            }

            // For a plain `aspire do` invocation, the extension host prompts the user for a step
            // later in GetRunArgumentsAsync, so don't add a validation error there.
            if (!ExtensionHelper.IsExtensionHost(InteractionService, out _, out _))
            {
                result.AddError(DoCommandStrings.StepArgumentRequired);
            }
        });
    }

    protected override string OperationCompletedPrefix => DoCommandStrings.OperationCompletedPrefix;
    protected override string OperationFailedPrefix => DoCommandStrings.OperationFailedPrefix;
    protected override string GetOutputPathDescription() => DoCommandStrings.OutputPathArgumentDescription;

    protected override string[] GetCommandArgs(ParseResult parseResult)
    {
        var step = parseResult.GetValue(_stepArgument);
        return !string.IsNullOrEmpty(step) ? [step] : [];
    }

    protected override async Task<string[]> GetRunArgumentsAsync(string? fullyQualifiedOutputPath, string[] unmatchedTokens, string? targetStep, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var operation = parseResult.GetValue(s_listStepsOption) ? "inspect" : "publish";
        var baseArgs = new List<string> { "--operation", operation };

        if (string.IsNullOrEmpty(targetStep)
            && !parseResult.GetValue(s_listStepsOption)
            && ExtensionHelper.IsExtensionHost(InteractionService, out _, out _))
        {
            targetStep = await InteractionService.PromptForStringAsync(
                DoCommandStrings.StepArgumentDescription,
                required: true,
                cancellationToken: cancellationToken);
        }

        if (!string.IsNullOrEmpty(targetStep))
        {
            baseArgs.AddRange(["--step", targetStep]);
        }

        if (fullyQualifiedOutputPath != null)
        {
            baseArgs.AddRange(["--output-path", fullyQualifiedOutputPath]);
        }

        // Add --log-level and --environment flags if specified
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

        return [.. baseArgs];
    }

    protected override string GetCanceledMessage() => DoCommandStrings.OperationCanceled;

    protected override string? GetTargetStepName(ParseResult parseResult) => parseResult.GetValue(_stepArgument);

    protected override string GetProgressMessage(ParseResult parseResult)
    {
        if (parseResult.GetValue(s_listStepsOption))
        {
            return "Listing pipeline steps";
        }

        var step = parseResult.GetValue(_stepArgument);
        return $"Executing step {step}";
    }
}
