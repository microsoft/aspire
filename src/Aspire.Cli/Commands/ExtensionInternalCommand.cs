// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal sealed class ExtensionInternalCommand : BaseCommand
{
    public ExtensionInternalCommand(IFeatures features, ICliUpdateNotifier updateNotifier, IProjectLocator projectLocator, CliExecutionContext executionContext, IInteractionService interactionService, AspireCliTelemetry telemetry) : base("extension", "Hidden command for extension integration", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        this.Hidden = true;
        this.Subcommands.Add(new GetAppHostCandidatesCommand(features, updateNotifier, projectLocator, executionContext, interactionService, telemetry));
    }

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return Task.FromResult(ExitCodeConstants.Success);
    }

    private sealed class GetAppHostCandidatesCommand : BaseCommand
    {
        private readonly IProjectLocator _projectLocator;

        public GetAppHostCandidatesCommand(IFeatures features, ICliUpdateNotifier updateNotifier, IProjectLocator projectLocator, CliExecutionContext executionContext, IInteractionService interactionService, AspireCliTelemetry telemetry) : base("get-apphosts", "Get AppHosts in the specified directory", features, updateNotifier, executionContext, interactionService, telemetry)
        {
            _projectLocator = projectLocator;
        }

        protected override bool UpdateNotificationsEnabled => false;

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _projectLocator.UseOrFindAppHostProjectFileAsync(null, MultipleAppHostProjectsFoundBehavior.None, createSettingsFile: false, cancellationToken);

                var json = JsonSerializer.Serialize(new AppHostProjectSearchResultPoco
                {
                    SelectedProjectFile = result.SelectedProjectFile?.FullName,
                    AllProjectFileCandidates = result.AllProjectFileCandidates.Select(f => f.FullName).ToList()
                }, BackchannelJsonSerializerContext.Default.AppHostProjectSearchResultPoco);
                // Structured output always goes to stdout.
                InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
                return ExitCodeConstants.Success;
            }
            catch
            {
                return ExitCodeConstants.FailedToFindProject;
            }
        }
    }
}

internal class AppHostProjectSearchResultPoco
{
    [JsonPropertyName("selected_project_file")]
    public string? SelectedProjectFile { get; init; }

    [JsonPropertyName("all_project_file_candidates")]
    public required List<string> AllProjectFileCandidates { get; init; }
}
