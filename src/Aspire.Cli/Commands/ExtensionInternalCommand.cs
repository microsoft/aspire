// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Commands;

internal sealed class ExtensionInternalCommand : BaseCommand
{
    public ExtensionInternalCommand(IProjectLocator projectLocator, CommonCommandServices services) : base("extension", "Hidden command for extension integration", services)
    {
        this.Hidden = true;
        this.Subcommands.Add(new GetAppHostCandidatesCommand(projectLocator, services));
    }

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return Task.FromResult(CommandResult.FromExitCode(CliExitCodes.Success));
    }

    private sealed class GetAppHostCandidatesCommand : BaseCommand
    {
        private readonly IProjectLocator _projectLocator;

        public GetAppHostCandidatesCommand(IProjectLocator projectLocator, CommonCommandServices services) : base("get-apphosts", "Get AppHosts in the specified directory", services)
        {
            _projectLocator = projectLocator;
        }

        protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
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
                return CommandResult.Success();
            }
            catch
            {
                return CommandResult.Failure(CliExitCodes.FailedToFindProject);
            }
        }
    }
}

// `aspire extension get-apphosts` is a hidden tooling output; keep
// docs/specs/cli-output-formats.md in sync when changing this shape.
internal class AppHostProjectSearchResultPoco
{
    [JsonPropertyName("selected_project_file")]
    public string? SelectedProjectFile { get; init; }

    [JsonPropertyName("all_project_file_candidates")]
    public required List<string> AllProjectFileCandidates { get; init; }
}
