// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;

namespace CreateFailingTestIssue;

public static class Program
{
    private const string DiagnosticsLogFileName = "diagnostics.log";

    public static Task<int> Main(string[] args)
    {
        var testOption = new Option<string?>("--test", "-t")
        {
            Description = "Canonical or display test name to resolve from .trx artifacts. Omit to emit all failing tests."
        };

        var urlOption = new Option<string?>("--url", "-u")
        {
            Description = "Source URL: PR, workflow run, or workflow job."
        };

        var workflowOption = new Option<string>("--workflow", "-w")
        {
            Description = "Workflow selector alias or workflow file path.",
            DefaultValueFactory = _ => "ci"
        };

        var repositoryOption = new Option<string>("--repo", "-r")
        {
            Description = "GitHub repository in owner/repo form.",
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "microsoft/aspire"
        };

        var forceNewOption = new Option<bool>("--force-new")
        {
            Description = "Bypass idempotent issue reuse and request a fresh issue."
        };

        var createOption = new Option<bool>("--create")
        {
            Description = "Create the issue on GitHub after generating content. Without this flag, the tool only generates the issue body and outputs JSON."
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Print the raw issue body markdown to stdout and exit. Does not create the issue or emit JSON."
        };

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Write JSON result to a file instead of stdout. Keeps output clean when dotnet build noise is interleaved."
        };

        var rootCommand = new RootCommand("Resolve one failing test, or emit all failing tests, from GitHub Actions artifacts.");
        rootCommand.Options.Add(testOption);
        rootCommand.Options.Add(urlOption);
        rootCommand.Options.Add(workflowOption);
        rootCommand.Options.Add(repositoryOption);
        rootCommand.Options.Add(forceNewOption);
        rootCommand.Options.Add(createOption);
        rootCommand.Options.Add(dryRunOption);
        rootCommand.Options.Add(outputOption);

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var commandInput = new CommandInput(
                TestQuery: parseResult.GetValue(testOption),
                SourceUrl: parseResult.GetValue(urlOption),
                WorkflowSelector: parseResult.GetValue(workflowOption)!,
                Repository: parseResult.GetValue(repositoryOption)!,
                ForceNew: parseResult.GetValue(forceNewOption),
                Create: parseResult.GetValue(createOption));

            var result = await ExecuteAsync(commandInput, cancellationToken).ConfigureAwait(false);
            result = await WriteDiagnosticsLogAsync(result, cancellationToken).ConfigureAwait(false);

            if (parseResult.GetValue(dryRunOption))
            {
                var body = result.Issue?.Body;
                if (!string.IsNullOrEmpty(body))
                {
                    Console.WriteLine(body);
                    return 0;
                }

                Console.Error.WriteLine(result.ErrorMessage ?? "No issue body was generated. Ensure --test is provided and matches a failing test.");
                return 1;
            }

            var json = JsonSerializer.Serialize(result, JsonSerializerOptions);

            var outputPath = parseResult.GetValue(outputOption);
            if (!string.IsNullOrEmpty(outputPath))
            {
                await File.WriteAllTextAsync(outputPath, json, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine(json);
            }

            return result.Success ? 0 : 1;
        });

        return rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task<CreateFailingTestIssueResult> ExecuteAsync(CommandInput input, CancellationToken cancellationToken)
    {
        try
        {
            return await FailingTestIssueCommand.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CreateFailingTestIssueResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Input = new InputSection(
                    TestQuery: input.TestQuery,
                    SourceUrl: input.SourceUrl,
                    Workflow: new WorkflowSection(
                        Requested: input.WorkflowSelector,
                        ResolvedWorkflowFile: string.Empty,
                        ResolvedWorkflowName: string.Empty),
                    ForceNew: input.ForceNew),
                Diagnostics = new DiagnosticsSection(
                    Log:
                    [
                        "Unhandled exception during command execution.",
                        ex.ToString()
                    ],
                    LogFile: DiagnosticsLogFileName,
                    Warnings: [ex.ToString()],
                    AvailableFailedTests: [])
            };
        }
    }

    private static async Task<CreateFailingTestIssueResult> WriteDiagnosticsLogAsync(CreateFailingTestIssueResult result, CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllLinesAsync(DiagnosticsLogFileName, result.Diagnostics.Log, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            return result with
            {
                Diagnostics = result.Diagnostics with
                {
                    Warnings = result.Diagnostics.Warnings.Concat([$"Failed to write {DiagnosticsLogFileName}: {ex.Message}"]).ToArray()
                }
            };
        }
    }

    private static JsonSerializerOptions JsonSerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
