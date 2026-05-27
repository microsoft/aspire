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

internal sealed class DoCommand : PipelineCommandBase
{
    internal override HelpGroup HelpGroup => HelpGroup.Deployment;

    private readonly Argument<string> _stepArgument;

    // Mirror of Aspire.Hosting.Pipelines.WellKnownPipelineSteps used only for the friendly
    // validation message when `aspire do --list-steps` is invoked without a step. The CLI
    // does not reference Aspire.Hosting, so this list is hand-maintained; keep in sync with
    // src/Aspire.Hosting/Pipelines/WellKnownPipelineSteps.cs. Sorted alphabetically so the
    // rendered error is easy to scan.
    private static readonly string[] s_wellKnownStepNames =
    [
        "before-start",
        "build",
        "build-prereq",
        "check-container-runtime",
        "deploy",
        "deploy-prereq",
        "destroy",
        "destroy-prereq",
        "diagnostics",
        "process-parameters",
        "publish",
        "publish-prereq",
        "push",
        "push-prereq",
        "validate-compute-environments"
    ];

    public DoCommand(IDotNetCliRunner runner, IInteractionService interactionService, IProjectLocator projectLocator, AspireCliTelemetry telemetry, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory, IConfiguration configuration, ILogger<DoCommand> logger, IAnsiConsole ansiConsole)
        : base("do", DoCommandStrings.Description, runner, interactionService, projectLocator, telemetry, features, updateNotifier, executionContext, hostEnvironment, projectFactory, configuration, logger, ansiConsole)
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
            if (string.IsNullOrEmpty(step) && !ExtensionHelper.IsExtensionHost(interactionService, out _, out _))
            {
                if (listSteps)
                {
                    // `aspire do --list-steps` with no step has no meaningful scope: the listing for
                    // `do` is always relative to a target step. Surface a friendly error pointing at
                    // concrete well-known step names rather than launching the AppHost and crashing
                    // mid-pipeline (see https://github.com/microsoft/aspire/issues/17526).
                    result.AddError(string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        DoCommandStrings.ListStepsRequiresStep,
                        string.Join(", ", s_wellKnownStepNames)));
                }
                else
                {
                    result.AddError(DoCommandStrings.StepArgumentRequired);
                }
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

    protected override async Task<string[]> GetRunArgumentsAsync(string? fullyQualifiedOutputPath, string[] unmatchedTokens, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var baseArgs = new List<string> { "--operation", "publish" };

        var step = parseResult.GetValue(_stepArgument);
        if (string.IsNullOrEmpty(step) && ExtensionHelper.IsExtensionHost(InteractionService, out _, out _))
        {
            step = await InteractionService.PromptForStringAsync(
                DoCommandStrings.StepArgumentDescription,
                required: true,
                cancellationToken: cancellationToken);
        }

        if (!string.IsNullOrEmpty(step))
        {
            baseArgs.AddRange(["--step", step]);
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
