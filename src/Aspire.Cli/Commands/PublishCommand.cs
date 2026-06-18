// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine.Parsing;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

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
            p => p,
            cancellationToken
        );
    }
}

internal sealed class PublishCommand : PublishCommandBase
{
    private readonly IPublishCommandPrompter _prompter;

    public PublishCommand(IDotNetCliRunner runner, IInteractionService interactionService, IProjectLocator projectLocator, IPublishCommandPrompter prompter, AspireCliTelemetry telemetry, IDotNetSdkInstaller sdkInstaller, IDotNetRuntimeSelector runtimeSelector, IFeatures features, ICliUpdateNotifier updateNotifier)
        : base("publish", PublishCommandStrings.Description, runner, interactionService, projectLocator, telemetry, sdkInstaller, runtimeSelector, features, updateNotifier)
    {
        ArgumentNullException.ThrowIfNull(prompter);
        _prompter = prompter;
    }

    protected override string OperationCompletedPrefix => PublishCommandStrings.OperationCompletedPrefix;
    protected override string OperationFailedPrefix => PublishCommandStrings.OperationFailedPrefix;

    protected override string GetOutputPathDescription() => PublishCommandStrings.OutputPathArgumentDescription;

    protected override string GetDefaultOutputPath(ArgumentResult result) => Path.Combine(Environment.CurrentDirectory);

    protected override string[] GetRunArguments(string fullyQualifiedOutputPath, string[] unmatchedTokens) =>
        ["--operation", "publish", "--publisher", "default", "--output-path", fullyQualifiedOutputPath, ..unmatchedTokens];

    protected override string GetCanceledMessage() => InteractionServiceStrings.OperationCancelled;

    protected override string GetProgressMessage() => PublishCommandStrings.GeneratingArtifacts;
}
