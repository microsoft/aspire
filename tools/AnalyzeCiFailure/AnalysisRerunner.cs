// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.TestTools;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Validates bound proposals and fresh GitHub state before optionally rerunning failed jobs.</summary>
internal static class AnalysisRerunner
{

    internal static async Task<int> RunAsync(
        string directory,
        EvidencePaths paths,
        string repository,
        string analysisDirectory,
        GitHubApiClient api,
        CancellationToken ct)
    {
        var outputPath = Path.GetFullPath(RequiredEnvironment("GH_AW_AGENT_OUTPUT", "for rerun authorization"), directory);
        var output = TransientJson.ReadUtf8<WorkflowAgentOutput>(await File.ReadAllBytesAsync(outputPath, ct));
        if (output.Items.ValueKind != JsonValueKind.Array)
        {
            throw new ValidationException("Agent output must contain an items array.");
        }
        var requests = new List<JsonElement>();
        foreach (var item in output.Items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String)
            {
                throw new ValidationException("Every agent output item must be an object with a string type.");
            }
            if (type.GetString() == "rerun_failed_jobs")
            {
                requests.Add(item);
            }
        }
        if (requests.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine("::notice::No rerun_failed_jobs items in agent output.");
            return 0;
        }
        if (requests.Count != 1)
        {
            throw new ValidationException("Exactly one rerun_failed_jobs request is permitted.");
        }
        var request = TransientJson.Read<WorkflowRerunRequest>(requests[0].GetRawText());
        ct.ThrowIfCancellationRequested();
        var analysis = EvidencePackageStore.ReadAndValidate(paths, analysisDirectory, repository);
        ct.ThrowIfCancellationRequested();
        var bundle = analysis.Bundle;
        var expectedPr = bundle.SubjectPr?.Number.ToString(CultureInfo.InvariantCulture) ?? "";
        if (request.RunId != bundle.Run.Id || (request.PrNumbers ?? "") != expectedPr ||
            analysis.Report.Verdict != AnalysisVerdict.TransientInfrastructure || analysis.Report.Tests.Length != 0)
        {
            throw new ValidationException("Rerun request does not match the validated transient infrastructure analysis.");
        }
        var id = bundle.Run.Id.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(Environment.GetEnvironmentVariable("ENABLE_RERUN"), "true", StringComparison.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine($"::notice::Dry-run mode: would have rerun failed jobs for {bundle.Run.Url} attempt={bundle.Run.Attempt}.");
            return 0;
        }
        var github = new AnalysisGitHubClient(api, ct);
        if (bundle.Run.Scope == RunScope.PullRequest)
        {
            if (bundle.SubjectPr is null)
            {
                Console.WriteLine($"::notice::No unambiguous subject PR for {bundle.Run.Url}; skipping rerun.");
                return 0;
            }
            try
            {
                if (!await new AnalysisPublicationQueries(github).IsPullRequestOpenAndUnlockedAsync(repository, expectedPr))
                {
                    Console.WriteLine($"::notice::PR #{expectedPr} is closed or locked; skipping rerun for {bundle.Run.Url}.");
                    return 0;
                }
            }
            catch (ValidationException ex)
            {
                Console.WriteLine($"::warning::Cannot check PR #{expectedPr}; skipping rerun for {bundle.Run.Url}: " +
                    Display(DiagnosticTextSanitizer.Sanitize(ex.Message, limit: 500)));
                return 0;
            }
        }
        JsonNode? current;
        try
        {
            current = await github.GetAsync($"repos/{repository}/actions/runs/{id}");
        }
        catch (Exception ex) when (ex is ValidationException or JsonException)
        {
            throw new ValidationException($"Unable to read current run {bundle.Run.Url} attempt={bundle.Run.Attempt}: " +
                Display(DiagnosticTextSanitizer.Sanitize(ex.Message, limit: 500)));
        }
        if (current is not JsonObject run || !GitHubJson.TryReadId(run["id"], out var currentId) || currentId != bundle.Run.Id ||
            run["run_attempt"] is not JsonValue attempt || !attempt.TryGetValue<int>(out var currentAttempt) || currentAttempt <= 0)
        {
            throw new ValidationException($"Invalid current workflow run metadata for {bundle.Run.Url}.");
        }
        if (currentAttempt != bundle.Run.Attempt)
        {
            Console.WriteLine($"::warning::Run attempt advanced for {bundle.Run.Url}; skipping stale rerun request.");
            return 0;
        }
        ct.ThrowIfCancellationRequested();
        try
        {
            await github.SendAsync(
                HttpMethod.Post,
                $"repos/{repository}/actions/runs/{id}/rerun-failed-jobs",
                body: null,
                HttpStatusCode.Created);
        }
        catch (ValidationException ex)
        {
            throw new ValidationException($"Unable to request rerun for {bundle.Run.Url} attempt={bundle.Run.Attempt}: " +
                Display(DiagnosticTextSanitizer.Sanitize(ex.Message, limit: 500)));
        }
        Console.WriteLine($"::notice::Requested rerun of failed jobs for {bundle.Run.Url} attempt={bundle.Run.Attempt}.");
        return 0;
    }

    private static string RequiredEnvironment(string name, string purpose)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
            : throw new ValidationException($"{name} is required {purpose}.");
}
