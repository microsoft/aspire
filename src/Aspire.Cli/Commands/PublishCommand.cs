// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Semver;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal interface IPublishCommandPrompter
{
    Task<string> PromptForPublisherAsync(IEnumerable<string> publishers, CancellationToken cancellationToken);
}

internal class PublishCommandPrompter(IInteractionService interactionService) : IPublishCommandPrompter
{
    public virtual async Task<string> PromptForPublisherAsync(IEnumerable<string> publishers, CancellationToken cancellationToken)
    {
        return await interactionService.PromptForSelectionAsync(
            PublishCommandStrings.SelectAPublisher,
            publishers,
            p => p.EscapeMarkup(),
            cancellationToken: cancellationToken
        );
    }
}

internal sealed class PublishCommand : PipelineCommandBase
{
    private const int MinimumHostingMajorVersionForVerification = 13;
    private const int MinimumHostingMinorVersionForVerification = 5;

    internal override HelpGroup HelpGroup => HelpGroup.Deployment;

    private readonly IPublishCommandPrompter _prompter;
    private readonly IGitRepository _gitRepository;
    private readonly ILogger<PublishCommand> _logger;
    private readonly Option<bool> _verifyOption;

    public PublishCommand(IDotNetCliRunner runner, IProjectLocator projectLocator, IPublishCommandPrompter prompter, IGitRepository gitRepository, IFeatures features, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory, IConfiguration configuration, ILogger<PublishCommand> logger, IAnsiConsole ansiConsole,
        CommonCommandServices services)
        : base("publish", PublishCommandStrings.Description, runner, projectLocator, features, hostEnvironment, projectFactory, configuration, logger, ansiConsole, services)
    {
        _prompter = prompter;
        _gitRepository = gitRepository;
        _logger = logger;
        _verifyOption = new Option<bool>("--verify")
        {
            Description = PublishCommandStrings.VerifyOptionDescription
        };
        Options.Add(_verifyOption);
    }

    protected override string OperationCompletedPrefix => PublishCommandStrings.OperationCompletedPrefix;
    protected override string OperationFailedPrefix => PublishCommandStrings.OperationFailedPrefix;
    protected override string GetOutputPathDescription() => PublishCommandStrings.OutputPathArgumentDescription;

    protected override Task<string[]> GetRunArgumentsAsync(string? fullyQualifiedOutputPath, string[] unmatchedTokens, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var baseArgs = new List<string> { "--operation", "publish", "--step", "publish" };

        if (fullyQualifiedOutputPath is not null)
        {
            baseArgs.AddRange(["--output-path", fullyQualifiedOutputPath]);
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

    protected override string GetCanceledMessage() => InteractionServiceStrings.OperationCancelled;

    protected override string? GetTargetStepName(ParseResult parseResult) => "publish";

    protected override string GetProgressMessage(ParseResult parseResult) => "Executing step publish";

    protected override string[] GetCommandArgs(ParseResult parseResult)
    {
        return parseResult.GetValue(_verifyOption) ? ["--verify"] : [];
    }

    protected override async Task<IPipelineExecutionSession?> CreateExecutionSessionAsync(
        FileInfo appHostFile,
        string? fullyQualifiedOutputPath,
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        if (!parseResult.GetValue(_verifyOption))
        {
            return null;
        }

        if (parseResult.GetValue(s_listStepsOption))
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyListStepsNotSupported);
        }

        var project = _projectFactory.GetProject(appHostFile);
        var hostingVersion = await project
            .GetAspireHostingVersionAsync(appHostFile, cancellationToken)
            .ConfigureAwait(false);
        if (!SupportsPublishVerification(hostingVersion))
        {
            throw new AppHostIncompatibleException(
                PublishCommandStrings.VerifyHostingIncompatible,
                "pipeline-outputs.v1",
                hostingVersion);
        }

        var regenerateArguments = BuildRegenerateArguments(
            appHostFile,
            fullyQualifiedOutputPath,
            parseResult);

        return await PublishVerificationSession.CreateAsync(
            appHostFile,
            fullyQualifiedOutputPath,
            _gitRepository,
            InteractionService,
            _logger,
            regenerateArguments,
            cancellationToken).ConfigureAwait(false);
    }

    private static string[] BuildRegenerateArguments(
        FileInfo appHostFile,
        string? fullyQualifiedOutputPath,
        ParseResult parseResult)
    {
        var logicalOutputPath = fullyQualifiedOutputPath
            ?? Path.Combine(appHostFile.DirectoryName!, "aspire-output");
        var arguments = new List<string>
        {
            "aspire",
            "publish",
            "--apphost",
            appHostFile.FullName,
            "--output-path",
            logicalOutputPath
        };

        var logLevel = parseResult.GetValue(s_pipelineLogLevelOption);
        if (!string.IsNullOrEmpty(logLevel))
        {
            arguments.AddRange(["--pipeline-log-level", logLevel]);
        }

        var environment = parseResult.GetValue(s_environmentOption);
        if (!string.IsNullOrEmpty(environment))
        {
            arguments.AddRange(["--environment", environment]);
        }

        if (parseResult.GetValue(s_includeExceptionDetailsOption))
        {
            arguments.Add("--include-exception-details");
        }

        if (parseResult.GetValue(s_noBuildOption))
        {
            arguments.Add("--no-build");
        }

        if (parseResult.UnmatchedTokens.Count > 0)
        {
            arguments.Add("--");
            arguments.AddRange(parseResult.UnmatchedTokens);
        }

        return [.. arguments];
    }

    private static bool SupportsPublishVerification(string? hostingVersion)
    {
        if (string.IsNullOrWhiteSpace(hostingVersion) ||
            !SemVersion.TryParse(hostingVersion, SemVersionStyles.Any, out var version))
        {
            return false;
        }

        return version.Major > MinimumHostingMajorVersionForVerification ||
            version.Major == MinimumHostingMajorVersionForVerification &&
            version.Minor >= MinimumHostingMinorVersionForVerification;
    }
}
