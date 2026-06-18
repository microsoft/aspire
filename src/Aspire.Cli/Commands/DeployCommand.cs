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

internal sealed class DeployCommand : PublishCommandBase
{
    public DeployCommand(IDotNetCliRunner runner, IInteractionService interactionService, IProjectLocator projectLocator, AspireCliTelemetry telemetry, IDotNetSdkInstaller sdkInstaller, IDotNetRuntimeSelector runtimeSelector, IFeatures features, ICliUpdateNotifier updateNotifier)
        : base("deploy", DeployCommandStrings.Description, runner, interactionService, projectLocator, telemetry, sdkInstaller, runtimeSelector, features, updateNotifier)
    {
    }

    protected override string OperationCompletedPrefix => DeployCommandStrings.OperationCompletedPrefix;
    protected override string OperationFailedPrefix => DeployCommandStrings.OperationFailedPrefix;

    protected override string GetOutputPathDescription() => DeployCommandStrings.OutputPathArgumentDescription;

    protected override string GetDefaultOutputPath(ArgumentResult result) => Path.Combine(Environment.CurrentDirectory, "deploy");

    protected override string[] GetRunArguments(string fullyQualifiedOutputPath, string[] unmatchedTokens) =>
        ["--operation", "publish", "--publisher", "default", "--output-path", fullyQualifiedOutputPath, "--deploy", "true", ..unmatchedTokens];

    protected override string GetCanceledMessage() => DeployCommandStrings.DeploymentCanceled;

    protected override string GetProgressMessage() => PublishCommandStrings.GeneratingArtifacts;
}
