// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;

namespace Infrastructure.Tests;

/// <summary>Exercises production Markdown and metadata projections through shared scenario helpers.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    // The fixtures exercise the production functions, with file I/O kept here only
    // to preserve the representative inputs and independently asserted outputs.
    private static Task<CommandResult> RunFunctionAsync(Func<string> operation, int errorCode = 1)
    {
        try
        {
            return Task.FromResult(new CommandResult(ExitCode: 0, operation()));
        }
        catch (Exception ex) when (ex is ValidationException or System.Text.Json.JsonException or IOException or InvalidDataException)
        {
            return Task.FromResult(new CommandResult(errorCode, (errorCode == 2 ? "::warning::" : "::error::") + ex.Message + "\n"));
        }
    }

    private static async Task<CommandResult> RunFunctionAsync(Func<Task<string>> operation)
    {
        try
        {
            return new CommandResult(ExitCode: 0, await operation());
        }
        catch (Exception ex) when (ex is ValidationException or System.Text.Json.JsonException or IOException)
        {
            return new CommandResult(ExitCode: 1, "::error::" + ex.Message + "\n");
        }
    }

    private static int Int(IReadOnlyList<string> args, int index, int fallback)
        => args.Count > index ? int.Parse(args[index], CultureInfo.InvariantCulture) : fallback;

    private static long Long(IReadOnlyList<string> args, int index, long fallback)
        => args.Count > index ? long.Parse(args[index], CultureInfo.InvariantCulture) : fallback;

    private static Task<CommandResult> RunCommentRendererAsync(IReadOnlyList<string> args)
        => RunFunctionAsync(() => PrCommentRenderer.Render(
            CiFailureJson.ReadContract<AnalysisReport>(args[0])!,
            CiFailureJson.ReadContract<WorkflowJob[]>(args[1])!,
            args[2]));

    private static Task<CommandResult> RunIssueRendererAsync(IReadOnlyList<string> args)
        => RunFunctionAsync(
            () =>
        {
            static T? Optional<T>(string file) where T : class
                => File.Exists(file) ? CiFailureJson.ReadContract<T>(file) : null;
            var evidence = new PublicationEvidence(
                PullRequest: null,
                Optional<WorkflowRun>(args[2]),
                Optional<MergedPullRequest>(args[3]),
                Optional<CandidateHistoryStatus>(args[4])?.State ?? CandidateHistoryState.Unavailable,
                Candidates: null);
            var projection = CauseIssueRenderer.RenderCanonicalIssue(
                (JsonObject)CiFailureJson.Read(args[0])!,
                Optional<RunContext>(args[1]),
                evidence,
                args[5],
                args[6],
                args[7],
                args[8],
                args[9]);
            CiFailureJson.Write(args[11], new JsonObject { ["title"] = projection.Title, ["labels"] = projection.Labels });
            File.WriteAllText(args[10], projection.Body);
            if (!(CauseIssueRenderer.FitIssue(projection.Body!, []) is not null))
            {
                throw new ValidationException("Rendered cause issue exceeds the 65000-byte publication budget");
            }
            return "";
        },
            errorCode: 2);

    private static Task<CommandResult> RunExtractionAsync(IReadOnlyList<string> args)
        => RunFunctionAsync(() =>
        {
            TestResultArchiveExtractor.Extract(
                args[0],
                args[1],
                Int(args, index: 2, fallback: 10000),
                Long(args, index: 3, fallback: 1073741824),
                Long(args, index: 4, fallback: 104857600),
                args.Count > 5 && args[5].Length > 0 ? Long(args, index: 5, fallback: 0) : null,
                args.Count > 6 ? args[6] : "trx");
            return "";
        });

    private static Task<CommandResult> RunSelectionAsync(IReadOnlyList<string> args)
        => RunFunctionAsync(() => TestResultArtifacts.SerializeSelection(TestResultArtifacts.Select(
            TestResultArtifacts.ReadArtifacts((JsonArray)CiFailureJson.Read(args[0])!),
            ((JsonArray)CiFailureJson.Read(args[3])!).OfType<JsonObject>()
                .SelectMany(job => new AspireCiWorkflowConventions().DescribeTestArtifacts(job, Int(args, index: 7, fallback: 1)))
                .ToArray(),
            args[1],
            args[2],
            Int(args, index: 4, fallback: 20),
            Long(args, index: 5, fallback: 1073741824),
            Long(args, index: 6, fallback: 104857600))).ToJsonString() + "\n");

    private static Task<CommandResult> RunJsonRenderingAsync(IReadOnlyList<string> args)
        => RunFunctionAsync(() => "    " + DiagnosticTextSanitizer.SanitizeJson(
            CiFailureJson.Read(args[0]),
            Int(args, index: 1, fallback: 500),
            args.Count > 2 && args[2] == "multiline")!.ToJsonString() + "\n");

    private static Task<CommandResult> RunTextRenderingAsync(IReadOnlyList<string> args)
        => RunFunctionAsync(() => MarkdownFormatting.Indent(DiagnosticTextSanitizer.Sanitize(File.ReadAllText(args[0]), Int(args, index: 1, fallback: 65536), multiline: true)) + "\n");

    private static Task<CommandResult> RunTextSanitizingAsync(IReadOnlyList<string> args)
        => RunFunctionAsync(() =>
        {
            File.WriteAllText(args[1], DiagnosticTextSanitizer.Sanitize(File.ReadAllText(args[0]), Int(args, index: 2, fallback: 10485760), multiline: true) + "\n");
            return "";
        });

    private static Task<CommandResult> RunOccurrencesAsync(IReadOnlyList<string> args)
        => RunFunctionAsync(
            () =>
        {
            var body = CauseIssueRenderer.AppendOccurrence(File.ReadAllText(args[0]), args[1], Int(args, index: 2, fallback: 1));
            var fitted = CauseIssueRenderer.FitIssue(body, [], Int(args, index: 4, fallback: 65000));
            if (!(fitted is not null))
            {
                throw new ValidationException("occurrence section cannot fit within the publication budget");
            }
            File.WriteAllText(args[3], fitted);
            return "";
        },
            errorCode: 2);

    private static Task<CommandResult> RunMainMigrationAsync(IReadOnlyList<string> args)
        => RunFunctionAsync(
            () =>
        {
            var body = CauseIssueRenderer.MigrateMainBody(File.ReadAllText(args[0]), File.ReadAllText(args[1]));
            var fitted = CauseIssueRenderer.FitIssue(body, [], Int(args, index: 3, fallback: 65000));
            if (!(fitted is not null))
            {
                throw new ValidationException("migrated issue body exceeds the publication budget");
            }
            File.WriteAllText(args[2], fitted);
            return "";
        },
            errorCode: 2);

    private static Task<CommandResult> RunJobNamesAsync(IReadOnlyList<string> args)
        => RunFunctionAsync(() =>
        {
            var cause = CiFailureJson.Read(args[0])!;
            var jobs = CiFailureJson.ToContract<System.Collections.Immutable.ImmutableArray<WorkflowJob>>(CiFailureJson.Read(args[1]));
            var names = ((JsonArray)cause["job_ids"]!).Select(id =>
                jobs.First(job => CiFailureJson.AsNormalizedNumber(job.Id) == CiFailureJson.ReadNormalizedNumber(id)).Name!);
            return CauseIssueRenderer.JobNames(names, args[2]) + "\n";
        });

    private Task<CommandResult> RunIssueInventoryAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment)
        => RunFunctionAsync(async () =>
        {
            File.Delete(args[1]);
            File.Delete(args[2]);
            var (open, closed) = await new AnalysisPublicationQueries(new AnalysisGitHubClient(CreateGitHubClient(environment), CancellationToken.None)).GetCauseIssuesAsync(args[0]);
            File.WriteAllText(args[1], open.ToJsonString() + "\n");
            File.WriteAllText(args[2], closed.ToJsonString() + "\n");
            return "";
        });

    private Task<CommandResult> RunActionabilityAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment)
        => RunFunctionAsync(async () => (await new AnalysisPublicationQueries(new AnalysisGitHubClient(CreateGitHubClient(environment), CancellationToken.None)).IsPullRequestOpenAndUnlockedAsync(args[0], args[1]))
            ? "true\n" : "false\n");

    private Task<CommandResult> RunCommentLookupAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment)
        => RunFunctionAsync(async () => await new AnalysisPublicationQueries(new AnalysisGitHubClient(CreateGitHubClient(environment), CancellationToken.None)).FindAnalysisCommentIdAsync(args[0], args[1]) + "\n");
}
