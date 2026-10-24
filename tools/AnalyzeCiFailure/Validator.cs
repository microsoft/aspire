// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static AnalyzeCiFailure.Json;
using static AnalyzeCiFailure.ValidationRules;

namespace AnalyzeCiFailure;

internal sealed class Validator(string workingDirectory, string scriptsDirectory, string analysisDirectory)
{
    private readonly PublicationScripts _scripts = new(workingDirectory, scriptsDirectory);
    private readonly string _analysisFile = Path.Combine(analysisDirectory, "analysis-result.json");
    private string Data(string file) => Path.Combine(workingDirectory, "ci-failure-data", file);

    public async Task ValidateAsync()
    {
        Require(new[] { _analysisFile, Data("run-context.json"), Data("failed-jobs.json"), Data("test-evidence.json"), Data("run.json") }.All(File.Exists),
            "Analysis result or trusted run data not found");
        var jobsFile = _analysisFile + ".trusted-failed-jobs.tmp";
        var testsFile = _analysisFile + ".trusted-test-failures.tmp";
        try
        {
            await _scripts.SanitizeInPlaceAsync("sanitize-analysis", _analysisFile);
            var document = Read(_analysisFile);

            // Preserve rejection order, including scope and cause-count guards before
            // deeper job/cause checks, so command diagnostics stay stable.
            var run = ReadRunIdentity(document, Read(Data("run-context.json")), Read(Data("run.json")));
            var analysis = ReadAnalysis(document);
            Require(await _scripts.SanitizeAsync("sanitize-trusted-failed-jobs", Data("failed-jobs.json"), jobsFile), "Trusted failed jobs are invalid");
            var jobs = ((JsonArray)Read(jobsFile)!).Select(j => new FailedJob(Number(Field(j, "id"))!.Value, Raw(Field(j, "id")), Text(Field(j, "name"))!)).ToImmutableArray();
            var trustedTests = await ReadTrustedTestsAsync(testsFile);
            var trustedPairs = trustedTests.Select(t => new TestIdentity(Text(Field(t, "test"))!, Text(Field(t, "job"))!)).ToImmutableArray();
            RequireExactTestCoverage(analysis, trustedPairs, jobs);
            RebindTrustedDiagnostics(document!, trustedTests);

            RequirePermittedVerdict(analysis, run);

            var causesDirectory = Path.Combine(analysisDirectory, "causes");
            var causeFiles = Directory.Exists(causesDirectory)
                ? Directory.GetFileSystemEntries(causesDirectory, "*.json", new EnumerationOptions { MatchCasing = MatchCasing.CaseSensitive })
                    .Where(f => !Path.GetFileName(f).StartsWith('.')).Order(StringComparer.Ordinal).ToArray()
                : [];
            RequireCauseBudget(analysis.CauseIds.Length, causeFiles.Length);
            RequireExactJobCoverage(analysis, jobs, run);

            var causes = ImmutableArray.CreateBuilder<FailureCause>();
            foreach (var file in causeFiles)
            {
                var cause = await ReadCauseAsync(file);
                ValidateCauseCompatibility(cause, analysis, jobs, run);
                ValidateStoredIdentity(cause, Data(Path.Combine("prior-causes", cause.FileName)));
                causes.Add(cause);
            }

            var validatedCauses = causes.ToImmutable();
            RequireExactCauseCoverage(analysis, validatedCauses);
            ValidateVerdict(analysis, validatedCauses, run);
            RequireCauseCoverage(analysis, validatedCauses, jobs);

            if (run.Scope == "pull-request" && Regex.IsMatch(run.PrNumbers, "^[1-9][0-9]*\\z"))
            {
                var commentBytes = await _scripts.CommentBytesAsync(_analysisFile, jobsFile, run.Url);
                RequireCommentBudget(commentBytes);
            }
        }
        finally
        {
            File.Delete(jobsFile);
            File.Delete(testsFile);
        }
    }

    private static ValidatedRun ReadRunIdentity(JsonNode? analysis, JsonNode? context, JsonNode? metadata)
    {
        var id = Raw(Field(context, "run_id"));
        var scope = ShellText(Field(context, "run_scope"));
        var url = Text(Field(metadata, "html_url")) ?? "";
        Require(Number(Field(metadata, "id")) is not null, "Trusted run metadata is invalid");
        var pr = Field(analysis, "pr");
        var prNumbers = RawFallback(Field(context, "pr_numbers"));
        var trusted = new TrustedRunIdentity(id, Raw(Field(metadata, "id")), scope, url, prNumbers);
        // Keep shell decoding at the boundary: e.g. "n\u0000ull" and "null\n\u0000"
        // compare equal to "null" for main, but do not normalize structured PR numbers.
        var proposed = new ProposedRunIdentity(
            Raw(Field(analysis, "run_id")),
            ShellText(Field(analysis, "run_scope")),
            ShellText(Fallback(pr)) != "null",
            pr is JsonObject && Number(Field(pr, "number")) is not null ? Raw(Field(pr, "number")) : null,
            analysis is JsonObject obj && obj.ContainsKey("pr") && pr is null);
        RequireRunIdentity(trusted, proposed);

        return new ValidatedRun(scope, url, prNumbers);
    }

    private static FailureAnalysis ReadAnalysis(JsonNode? document)
    {
        var jobNodes = Field(document, "failed_jobs") as JsonArray;
        var causeNodes = Field(document, "causes") as JsonArray;
        Require(jobNodes is not null && jobNodes.All(j => Number(Field(j, "id")) is not null) &&
            causeNodes is not null && causeNodes.All(c => Text(c) is not null),
            "Analysis must contain numeric-ID failed_jobs and string-valued causes arrays");
        var tests = Field(document, "failed_tests") as JsonArray;
        Require(jobNodes!.All(j => Safe(Fallback(Field(j, "reason")) ?? JsonValue.Create(""), 500)) &&
            tests is not null && tests.All(t => t is JsonObject &&
                Safe(Field(t, "name"), 500) && Text(Field(t, "name"))!.Length > 0 &&
                Safe(Field(t, "job"), 500) && Text(Field(t, "job"))!.Length > 0 &&
                Safe(Field(t, "error"), 1000, multiline: true) &&
                OptionalDiagnostic(t, "stack_trace", 2000) && OptionalDiagnostic(t, "standard_output", 4000) &&
                OptionalDiagnostic(t, "standard_error", 4000) &&
                Text(Field(t, "classification")) is "flaky" or "code-issue" && Safe(Field(t, "reason"), 500)),
            "Analysis failed_tests must match the safe field schema");

        return new FailureAnalysis(
            ShellText(Field(document, "verdict")),
            jobNodes!.Select(j => new ClassifiedJob(Number(Field(j, "id"))!.Value, Raw(Field(j, "id")), Text(Field(j, "classification")) ?? "")).ToImmutableArray(),
            tests!.Select(t => new FailedTest(Text(Field(t, "name"))!, Text(Field(t, "job"))!, Text(Field(t, "classification"))!)).ToImmutableArray(),
            causeNodes!.Select(c => Text(c)!).ToImmutableArray());
    }

    private static bool OptionalDiagnostic(JsonNode? test, string field, int max)
        => Field(test, field) is not { } node || Safe(node, max, multiline: true);

    private async Task<JsonArray> ReadTrustedTestsAsync(string testsFile)
    {
        var state = RawFallback(Field(Read(Data("test-evidence.json")), "state"));
        Require(state != "unavailable", "Trusted test evidence is unavailable");
        Require(state is "complete" or "not-applicable", "Trusted test evidence state is invalid");
        if (state == "not-applicable")
        {
            return [];
        }

        Require(File.Exists(Data("test-failures.json")) &&
            await _scripts.SanitizeAsync("sanitize-trusted-test-failures", Data("test-failures.json"), testsFile), TestMismatch);

        return (JsonArray)Read(testsFile)!;
    }

    private void RebindTrustedDiagnostics(JsonNode document, JsonArray trusted)
    {
        var tests = (JsonArray)document["failed_tests"]!;
        if (tests.Count > 0)
        {
            // Exact (test, job) binding preserves parameterized names and disambiguates identical
            // display names in different jobs. Only trusted diagnostics may reach publication.
            var byPair = trusted.ToDictionary(t => (Text(Field(t, "test"))!, Text(Field(t, "job"))!));
            foreach (var test in tests)
            {
                var evidence = byPair[(Text(test!["name"])!, Text(test["job"])!)];
                foreach (var field in new[] { "error", "stack_trace", "standard_output", "standard_error" })
                {
                    test[field] = evidence![field]!.DeepClone();
                }
            }
            Write(_analysisFile, document);
        }
    }

    private async Task<FailureCause> ReadCauseAsync(string file)
    {
        var name = Path.GetFileName(file);
        Require(!Directory.Exists(file), $"Invalid JSON in cause file: {Display(name)}");
        try
        {
            Read(file);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ValidationException($"Invalid JSON in cause file: {Display(name)}");
        }

        await _scripts.SanitizeInPlaceAsync("sanitize-cause", file);
        var node = Read(file);
        var ids = Field(node, "job_ids") as JsonArray;
        var type = Text(Field(node, "type"));
        var testName = Fallback(Field(node, "test_name")) ?? JsonValue.Create("");
        Require(node is JsonObject obj && obj.All(p => p.Key is "error_pattern" or "id" or "job_ids" or "test_name" or "title" or "type") &&
            Text(Field(node, "id")) is not null && type is not null &&
            Safe(Field(node, "title"), 238) && Visible(Text(Field(node, "title"))!) &&
            Safe(Field(node, "error_pattern"), 500, multiline: true) && Visible(Text(Field(node, "error_pattern"))!) &&
            ids is not null && ids.All(IsPositiveInteger) && Safe(testName, 500),
            $"Cause {Display(name)} contains unsupported or publisher-owned fields");
        var id = Text(node!["id"])!;
        var cause = new FailureCause(id, name, type!, Text(testName)!, ids!.Select(i => Number(i)!.Value).ToImmutableArray());
        RequireCauseFields(cause);
        Require(Regex.IsMatch(id, "^[a-z0-9]+(-[a-z0-9]+)*\\z") && id + ".json" == name,
            $"Cause ID must be a lowercase hyphenated slug matching its filename: {Display(name)}");

        return cause;
    }

    private static bool Visible(string text) => text.EnumerateRunes().Any(r => !System.Text.Rune.IsWhiteSpace(r));

    private static void ValidateStoredIdentity(FailureCause cause, string file)
    {
        if (!File.Exists(file))
        {
            return;
        }

        var prior = Read(file);
        RequireUnchangedCauseIdentity(cause, new StoredCauseIdentity(
            RawFallback(Field(prior, "type")), Text(Field(prior, "test_name")) ?? ""));
    }
}
