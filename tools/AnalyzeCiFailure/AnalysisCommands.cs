// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.TestTools;
using static AnalyzeCiFailure.CommandLineOptions;
using CliCommand = System.CommandLine.Command;

namespace AnalyzeCiFailure;

/// <summary>Defines collection, replay, validation and publication commands and their invocation boundaries.</summary>
internal static class AnalysisCommands
{
    internal static RootCommand CreateRootCommand(Func<GitHubApiClient> createGitHubClient)
    {
        var workingDirectoryOption = CreateSingleUseStringOption("--working-dir", "Repository checkout directory used for relative paths and subprocesses. Defaults to the current directory.");

        var analysisDirectoryOption = CreateSingleUseStringOption("--analysis-dir", "Directory containing the downloaded analysis proposal.");
        analysisDirectoryOption.Required = true;
        var evidenceDirectoryOption = CreateSingleUseStringOption("--evidence-dir", "Explicit directory containing captured evidence. Required for replay, validation and publication.");
        evidenceDirectoryOption.Required = true;
        var githubOutputOption = CreateSingleUseStringOption("--github-output", "Write GitHub Actions step outputs to this file; this does not select the evidence output directory.");

        var historyDirectoryOption = CreateSingleUseStringOption("--history-dir", "New history checkout directory. Defaults to a private temporary directory; an existing checkout is rejected.");
        var runIdOption = CreateSingleUseStringOption("--run-id", "Workflow run to collect.");
        runIdOption.Required = true;
        var runAttemptOption = CreateSingleUseStringOption("--run-attempt", "Pinned run attempt. Omit to collect the latest attempt.");

        var repositoryOption = CreateSingleUseStringOption("--repo", "Repository in owner/name form.");
        repositoryOption.Required = true;

        var observedAtOption = CreateSingleUseStringOption("--observed-at", "Observation timestamp. Defaults to the current UTC time.");

        var collectCommand = new CliCommand("collect", "Fetch CI failure evidence from GitHub and generate analysis views.");
        var replayCommand = new CliCommand("replay", "Regenerate analysis views from captured evidence without contacting GitHub.");
        var validateCommand = new CliCommand("validate", "Validate analysis against trusted CI failure evidence.");
        var publishCommand = new CliCommand("publish", "Validate analysis, then publish CI failure history and issues.");
        var rootCommand = new RootCommand("Analyze CI failures using trusted evidence.");
        rootCommand.Options.Remove(rootCommand.Options.OfType<VersionOption>().Single());
        foreach (var command in new[] { collectCommand, replayCommand, validateCommand, publishCommand })
        {
            command.Options.Add(workingDirectoryOption);
            rootCommand.Subcommands.Add(command);
        }
        rootCommand.Subcommands.Add(CreateWorkflowCommand(createGitHubClient));

        collectCommand.Options.Add(repositoryOption);
        collectCommand.Options.Add(runIdOption);
        collectCommand.Options.Add(runAttemptOption);
        collectCommand.Options.Add(githubOutputOption);
        replayCommand.Options.Add(evidenceDirectoryOption);
        replayCommand.Options.Add(githubOutputOption);

        validateCommand.Options.Add(analysisDirectoryOption);
        validateCommand.Options.Add(evidenceDirectoryOption);
        validateCommand.Options.Add(repositoryOption);

        publishCommand.Options.Add(analysisDirectoryOption);
        publishCommand.Options.Add(evidenceDirectoryOption);
        publishCommand.Options.Add(githubOutputOption);
        publishCommand.Options.Add(historyDirectoryOption);
        publishCommand.Options.Add(repositoryOption);
        publishCommand.Options.Add(observedAtOption);

        collectCommand.SetAction((parseResult, ct) => ExecuteAsync(
            parseResult.GetValue(workingDirectoryOption),
            async workingDirectory =>
        {
            var output = parseResult.GetValue(githubOutputOption);
            if (output is not null)
            {
                if (output.Length == 0)
                {
                    throw new ValidationException("--github-output must name a nonempty file path.");
                }
                output = GetAbsolutePath(output, workingDirectory);
                if (Directory.Exists(output))
                {
                    throw new ValidationException("--github-output must name a file, not a directory.");
                }
            }
            using var invocation = new AnalysisInvocation();
            await invocation.WritePathsAsync("collection", output);
            return await EvidencePreparation.CollectAsync(
                workingDirectory,
                parseResult.GetValue(repositoryOption)!,
                invocation,
                parseResult.GetValue(runIdOption),
                parseResult.GetValue(runAttemptOption),
                output,
                createGitHubClient,
                ct);
        }));

        replayCommand.SetAction((parseResult, ct) => ExecuteAsync(
            parseResult.GetValue(workingDirectoryOption),
            async workingDirectory =>
        {
            var input = GetEvidencePaths(parseResult.GetValue(evidenceDirectoryOption), workingDirectory);
            using var replay = new AnalysisInvocation();
            await replay.WritePathsAsync(
                "replay",
                parseResult.GetValue(githubOutputOption) is { } replayOutput
                ? GetAbsolutePath(replayOutput, workingDirectory) : null);
            return await EvidencePreparation.ReplayAsync(input, replay, ct);
        }));

        validateCommand.SetAction((parseResult, ct) => ExecuteAsync(
            parseResult.GetValue(workingDirectoryOption),
            workingDirectory =>
        {
            var analysisDirectory = GetAnalysisDirectory(parseResult.GetValue(analysisDirectoryOption), workingDirectory);
            ct.ThrowIfCancellationRequested();
            _ = EvidencePackageStore.ReadAndValidate(
                GetEvidencePaths(parseResult.GetValue(evidenceDirectoryOption), workingDirectory),
                analysisDirectory,
                parseResult.GetValue(repositoryOption)!);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(result: 0);
        }));

        publishCommand.SetAction((parseResult, ct) => ExecuteAsync(
            parseResult.GetValue(workingDirectoryOption),
            async workingDirectory =>
        {
            var analysisDirectory = GetAnalysisDirectory(parseResult.GetValue(analysisDirectoryOption), workingDirectory);
            var evidencePaths = GetEvidencePaths(parseResult.GetValue(evidenceDirectoryOption), workingDirectory);
            using var invocation = new AnalysisInvocation();
            await invocation.WritePathsAsync(
                "publication",
                parseResult.GetValue(githubOutputOption) is { } output
                ? GetAbsolutePath(output, workingDirectory) : null);
            var scratch = invocation.Scratch;
            var historyDirectory = parseResult.GetValue(historyDirectoryOption) is { } historyPath
                ? GetAbsolutePath(historyPath, workingDirectory)
                : scratch.HistoryCheckout;

            var progress = invocation.Progress;
            progress.Stage = "validation";
            var capture = invocation.Capture;
            try
            {
                using var github = createGitHubClient();
                ValidateHistoryCheckoutLocation(historyDirectory, evidencePaths.RootDirectory, analysisDirectory);
                ct.ThrowIfCancellationRequested();
                var analysis = EvidencePackageStore.ReadAndValidate(evidencePaths, analysisDirectory, parseResult.GetValue(repositoryOption)!);
                capture.Bundle = analysis.Bundle;
                progress.RunId = analysis.Bundle!.Run.Id;
                progress.RunAttempt = analysis.Bundle.Run.Attempt;
                progress.RunUrl = analysis.Bundle.Run.Url;
                progress.HeadSha = analysis.Bundle.Run.HeadSha;
                progress.RunScope = analysis.Bundle.Run.Scope;
                progress.SubjectPrNumber = analysis.Bundle.SubjectPr?.Number;
                progress.HasRelevantFailures = true;
                progress.Stage = "publication";
                await new AnalysisPublisher(
                    workingDirectory,
                    scratch.Requests,
                    historyDirectory,
                    parseResult.GetValue(repositoryOption)!,
                    parseResult.GetValue(observedAtOption) ??
                        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                    progress.Diagnostics,
                    github)
                    .PublishAsync(analysis, ct);
                await invocation.WriteDiagnosticsAsync("published");
            }
            catch (Exception error)
            {
                capture.Failure = error;
                progress.RecordProblem(AnalysisProblemCategory.Fatal, progress.Stage, error.Message);
                await invocation.WriteRecoveryBestEffortAsync("publication", "failed");
                await invocation.WriteDiagnosticsBestEffortAsync("failed");
                throw;
            }

            return 0;
        }));

        return rootCommand;
    }

    private static string GetAnalysisDirectory(string? analysisDirectory, string workingDirectory)
    {
        if (string.IsNullOrEmpty(analysisDirectory))
        {
            throw new ValidationException("--analysis-dir must not be empty");
        }

        return GetAbsolutePath(analysisDirectory!, workingDirectory);
    }

    private static string GetAbsolutePath(string path, string? baseDirectory)
    {
        try
        {
            return baseDirectory is null ? Path.GetFullPath(path) : Path.GetFullPath(path, baseDirectory);
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException($"Invalid path: {ex.Message}");
        }
    }

    internal static EvidencePaths GetEvidencePaths(string? path, string workingDirectory)
        => string.IsNullOrEmpty(path) ? throw new ValidationException("--evidence-dir is required for this command.")
            : new(GetAbsolutePath(path, workingDirectory));

    private static void ValidateHistoryCheckoutLocation(string historyDirectory, params string[] inputDirectories)
    {
        // History is a writable Git checkout; evidence and proposals are read-only inputs.
        // Resolve existing directory symlinks before checking containment so another
        // spelling of an input path cannot place the checkout inside that input tree.
        var destination = ResolvePhysicalPath(historyDirectory);
        foreach (var input in inputDirectories)
        {
            var relative = Path.GetRelativePath(ResolvePhysicalPath(input), destination);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new ValidationException("--history-dir must be outside the evidence and proposal input directories.");
            }
        }

    }

    private static string ResolvePhysicalPath(string path)
    {
        var activeLinks = new HashSet<string>(StringComparer.Ordinal);
        return ResolveDirectoryLinks(path);

        string ResolveDirectoryLinks(string currentPath)
        {
            var full = Path.GetFullPath(currentPath);
            var root = Path.GetPathRoot(full)!;
            var resolved = root;
            foreach (var part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
            {
                resolved = Path.Combine(resolved, part);
                if (Directory.Exists(resolved))
                {
                    var target = new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true);
                    if (target is not null)
                    {
                        var link = resolved;
                        if (!activeLinks.Add(link))
                        {
                            throw new ValidationException("History checkout path contains a symbolic-link cycle.");
                        }
                        // Resolving the final link does not canonicalize links in its target's
                        // parents, e.g. a target under /var when /var itself links to /private/var.
                        resolved = ResolveDirectoryLinks(target.FullName);
                        activeLinks.Remove(link);
                    }
                }
            }
            return Path.GetFullPath(resolved);
        }
    }

    internal static async Task<int> ExecuteAsync(string? workingDirectory, Func<string, Task<int>> action)
    {
        try
        {
            var directory = GetAbsolutePath(workingDirectory ?? Directory.GetCurrentDirectory(), baseDirectory: null);

            return await action(directory);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"::error::{CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(ex.Message, limit: 1000))}");

            return 1;
        }
    }

    private static Command CreateWorkflowCommand(Func<GitHubApiClient> createGitHubClient)
    {
        var group = new Command("workflow", "Run GitHub Actions completion checks and rerun authorization.");
        var gate = new Command("check-completion", "Check that known relevant failures reached an accepted terminal outcome using collection and publication job results.");
        gate.Aliases.Add("require-publication");
        var rerun = new Command("rerun", "Validate fresh evidence and authorize the requested rerun.");
        var workingDirectory = CommandLineOptions.CreateSingleUseStringOption("--working-dir", "Repository checkout directory used for relative paths and subprocesses. Defaults to the current directory.");
        var repository = CommandLineOptions.CreateSingleUseStringOption("--repo", "Repository in owner/name form.");
        repository.Required = true;
        var analysisDirectory = CommandLineOptions.CreateSingleUseStringOption("--analysis-dir", "Directory containing the downloaded proposal.");
        analysisDirectory.Required = true;
        var evidenceDirectory = CommandLineOptions.CreateSingleUseStringOption("--evidence-dir", "Directory containing independently collected evidence.");
        evidenceDirectory.Required = true;
        foreach (var command in new[] { gate, rerun })
        {
            command.Options.Add(workingDirectory);
            group.Subcommands.Add(command);
        }
        rerun.Options.Add(repository);
        rerun.Options.Add(analysisDirectory);
        rerun.Options.Add(evidenceDirectory);
        gate.SetAction((result, ct) => AnalysisCommands.ExecuteAsync(
            result.GetValue(workingDirectory),
            _ =>
        {
            ct.ThrowIfCancellationRequested();
            GitHubActionsIntegration.EnsureExpectedCompletion(
                Environment.GetEnvironmentVariable("HAS_WORK"),
                Environment.GetEnvironmentVariable("PUBLICATION_RESULT"),
                Environment.GetEnvironmentVariable("COLLECTION_STATUS"),
                Environment.GetEnvironmentVariable("COLLECTION_RESULT"));
            return Task.FromResult(result: 0);
        }));
        rerun.SetAction((result, ct) => AnalysisCommands.ExecuteAsync(
            result.GetValue(workingDirectory),
            async directory =>
            {
                using var github = createGitHubClient();
                return await AnalysisRerunner.RunAsync(
                    directory,
                    AnalysisCommands.GetEvidencePaths(result.GetValue(evidenceDirectory), directory),
                    result.GetValue(repository)!,
                    Path.GetFullPath(result.GetValue(analysisDirectory)!, directory),
                    github,
                    ct);
            }));
        return group;
    }

}
