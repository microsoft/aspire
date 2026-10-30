// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class CreatePullRequestActionTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["bash"])]
    public async Task ExistingPullRequestMetadataIsUpdatedOnlyWhenEnabled()
    {
        var action = ReadYaml(Path.Combine(RepoRoot.Path, ".github", "actions", "create-pull-request", "action.yml"));
        var runs = (YamlMappingNode)action.Children["runs"];
        var steps = (YamlSequenceNode)runs.Children["steps"];
        var createPrStep = Assert.Single(steps.Children.Cast<YamlMappingNode>(), step =>
            step.Children.TryGetValue("id", out var id) && ((YamlScalarNode)id).Value == "create-pr");
        var script = ((YamlScalarNode)createPrStep.Children["run"]).Value!;

        const string title = "[automated] Update actionlint to v1.7.13";
        const string body = "Updates the actionlint pin to v1.7.13.\n\n- Verified release digest.";

        using var workspace = TemporaryWorkspace.Create(output);
        var disabled = await RunCreatePrScriptAsync(script, workspace.Path, updateMetadata: false, title, body);

        Assert.Equal(0, disabled.ExitCode);
        Assert.Contains("pull-request-operation=none", await File.ReadAllTextAsync(disabled.OutputPath));
        Assert.False(File.Exists(disabled.EditArgumentsPath));

        var enabled = await RunCreatePrScriptAsync(script, workspace.Path, updateMetadata: true, title, body);

        Assert.Equal(0, enabled.ExitCode);
        Assert.Contains("pull-request-operation=updated", await File.ReadAllTextAsync(enabled.OutputPath));
        Assert.Equal(
            ["pr", "edit", "20610", "--title", title, "--body-file", "-"],
            await File.ReadAllLinesAsync(enabled.EditArgumentsPath));
        Assert.Equal(body + "\n", await File.ReadAllTextAsync(enabled.EditBodyPath));
    }

    private async Task<ScriptResult> RunCreatePrScriptAsync(
        string actionScript,
        string workingDirectory,
        bool updateMetadata,
        string title,
        string body)
    {
        var outputPath = Path.Combine(workingDirectory, $"github-output-{Guid.NewGuid():N}.txt");
        var editArgumentsPath = Path.Combine(workingDirectory, $"edit-arguments-{Guid.NewGuid():N}.txt");
        var editBodyPath = Path.Combine(workingDirectory, $"edit-body-{Guid.NewGuid():N}.txt");
        var script = $$"""
            set -euo pipefail

            gh() {
              if [[ "$1" == "pr" && "$2" == "list" ]]; then
                printf '%s\n' '{"number":20610,"url":"https://github.com/microsoft/aspire/pull/20610"}'
                return 0
              fi
              if [[ "$1" == "pr" && "$2" == "edit" ]]; then
                printf '%s\n' "$@" > "$EDIT_ARGUMENTS_PATH"
                cat > "$EDIT_BODY_PATH"
                return 0
              fi
              printf 'Unexpected gh invocation: %s\n' "$*" >&2
              return 97
            }

            # Drain stdin like the real jq so `echo ... | jq` cannot fail with
            # SIGPIPE under `set -o pipefail` when this fake exits first.
            jq() {
              cat > /dev/null
              case "$2" in
                .number) printf '20610\n' ;;
                .url) printf 'https://github.com/microsoft/aspire/pull/20610\n' ;;
                *) printf 'Unexpected jq query: %s\n' "$2" >&2; return 98 ;;
              esac
            }

            {{actionScript}}
            """;

        var result = await ProcessRunner.RunAsync(
            output,
            "bash",
            ["-c", script],
            workingDirectory,
            new Dictionary<string, string>
            {
                ["BRANCH"] = "update-actionlint",
                ["BASE"] = "main",
                ["PR_TITLE"] = title,
                ["PR_BODY"] = body,
                ["UPDATE_EXISTING_PR_METADATA"] = updateMetadata ? "true" : "false",
                ["EDIT_ARGUMENTS_PATH"] = editArgumentsPath,
                ["EDIT_BODY_PATH"] = editBodyPath,
                ["GITHUB_OUTPUT"] = outputPath,
                ["GITHUB_REPOSITORY"] = "microsoft/aspire",
                ["GH_TOKEN"] = "test-token",
            });

        return new ScriptResult(result.ExitCode, outputPath, editArgumentsPath, editBodyPath);
    }

    private static YamlMappingNode ReadYaml(string path)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(path)));
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    private sealed record ScriptResult(int ExitCode, string OutputPath, string EditArgumentsPath, string EditBodyPath);
}
