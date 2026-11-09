// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Exercises structured-result projection, copied-execution consolidation and diagnosis input generation.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    private AnalyzeCiFailure.EvidenceBundle? _preparationEvidence;
    [Theory]
    [InlineData("job-name")]
    [InlineData("parser-detail")]
    [InlineData("prior-cause")]
    public async Task PreparationRedactsUntrustedValuesAtDiagnosticBoundaries(string source)
    {
        var directory = await WritePreparationFixtureAsync();
        var paths = new AnalyzeCiFailure.EvidencePaths(EvidenceDirectory);
        await File.WriteAllTextAsync(Path.Combine(directory, "00001.trx"), "<TestRun><Results/></TestRun>");
        if (source == "job-name")
        {
            var artifacts = JsonNode.Parse(await File.ReadAllTextAsync(paths.SelectedTestArtifacts))!;
            artifacts[0]!["job"] = "Unknown token=diagnostic-secret; " + new string('x', count: 550);
            await File.WriteAllTextAsync(paths.SelectedTestArtifacts, artifacts.ToJsonString());
        }
        else if (source == "parser-detail")
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "00001.trx"),
                """<TestRun><ResultSummary><Counters total="token=diagnostic-secret" /></ResultSummary></TestRun>""");
        }
        else
        {
            Directory.CreateDirectory(paths.PriorCausesDirectory);
            await File.WriteAllTextAsync(
                paths.PriorCause("invalid-type.json"),
                """{"id":"invalid-type","type":"token=diagnostic-secret"}""");
        }

        var result = await RunPreparationAsync();
        var diagnostic = source == "prior-cause"
            ? JsonNode.Parse(await File.ReadAllTextAsync(paths.PriorCauseIndex))![0]!["read_error"]!.GetValue<string>()
            : result.Output;
        Assert.False(diagnostic.Contains("diagnostic-secret", StringComparison.Ordinal), diagnostic);
        if (source == "job-name")
        {
            Assert.Contains("[REDACTED]", diagnostic, StringComparison.Ordinal);
            Assert.Contains(new string('x', count: 450), diagnostic, StringComparison.Ordinal);
            Assert.False(diagnostic.Contains(new string('x', count: 501), StringComparison.Ordinal), diagnostic);
        }
        else if (source == "parser-detail")
        {
            Assert.Contains("[REDACTED]", diagnostic, StringComparison.Ordinal);
        }
        else
        {
            // The enum decoder reports the JSON location rather than echoing the invalid value.
            Assert.StartsWith("Cannot summarize prior cause record: ", diagnostic);
        }
        Assert.Equal(source == "prior-cause" ? 0 : 1, result.ExitCode);
    }

    [Theory]
    [InlineData("jobs-array", "Cannot read failed-jobs.json: expected an array of failed jobs.")]
    [InlineData("unnamed-job", "Cannot read failed-jobs.json: entry 1 must be a job object with a string 'name'.")]
    [InlineData("artifacts-array", "Cannot read selected-test-result-artifacts.json: expected an array of selected test artifacts.")]
    [InlineData("null-artifact", "Cannot read selected-test-result-artifacts.json: entry 1 must be a test artifact object.")]
    [InlineData("missing-job", "Cannot read selected-test-result-artifacts.json: artifact 8 is missing its 'job' name.")]
    [InlineData("unknown-job", "Cannot associate test artifact 8 with a failed job: expected one job named 'Missing job', but found 0.")]
    [InlineData("duplicate-job", "Cannot associate test artifact 8 with a failed job: expected one job named 'Tests', but found 2.")]
    [InlineData("job-id", "Cannot read failed-jobs.json: job 'Tests' must have a positive numeric 'id'.")]
    [InlineData("artifact-id", "Cannot read selected-test-result-artifacts.json: entry 1 must have a positive numeric 'id'.")]
    [InlineData("format", "Cannot read selected-test-result-artifacts.json: artifact 8 is missing its 'format' ('trx' or 'mocha').")]
    [InlineData("missing-directory", "Cannot read test results for artifact 8: extracted directory 'extracted-results/8' does not exist.")]
    [InlineData("missing-trx", "Cannot read TRX results for artifact 8: no '*.trx' files were found in 'extracted-results/8'.")]
    public async Task PreparationDiagnosticsIdentifyTheBrokenInputAndExpectedValue(string scenario, string diagnostic)
    {
        if (scenario is not ("unknown-job" or "duplicate-job" or "missing-directory" or "missing-trx"))
        {
            await WriteGroupedFixtureAsync();
            var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
            var document = JsonNode.Parse(AnalyzeCiFailure.TransientJson.Write(evidence))!;
            var failure = document["test_evidence"]!["failures"]![0]!;
            switch (scenario)
            {
                case "jobs-array": document["jobs"] = null; break;
                case "unnamed-job": document["jobs"]![0]!.AsObject().Remove("name"); break;
                case "artifacts-array": failure["sources"] = null; break;
                case "null-artifact": failure["sources"]![0] = null; break;
                case "missing-job": failure.AsObject().Remove("job_id"); break;
                case "job-id": document["jobs"]![0]!.AsObject().Remove("id"); break;
                case "artifact-id": failure["sources"]![0]!.AsObject().Remove("artifact_id"); break;
                case "format": failure["sources"]![0]!.AsObject().Remove("format"); break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            var rejected = await RunStrictValidationAsync(document.ToJsonString(), AnalyzeCiFailure.TransientJson.Write(proposal));
            Assert.Equal(1, rejected.ExitCode);
            Assert.Single(GetWorkflowCommandLines(rejected.Output));
            Assert.False(Directory.Exists(Path.Combine(_workspace.Path, "memory-repo")));
            return;
        }
        var results = await WritePreparationFixtureAsync();
        var resultFile = Path.Combine(results, "00001.trx");
        await File.WriteAllTextAsync(resultFile, "<TestRun><Results/></TestRun>");
        var paths = new AnalyzeCiFailure.EvidencePaths(EvidenceDirectory);
        var selected = JsonNode.Parse(await File.ReadAllTextAsync(paths.SelectedTestArtifacts))!.AsArray();
        switch (scenario)
        {
            case "jobs-array":
                await File.WriteAllTextAsync(paths.FailedJobs, "null");
                break;
            case "unnamed-job":
                await File.WriteAllTextAsync(paths.FailedJobs, """[{"id":1}]""");
                break;
            case "artifacts-array":
                await File.WriteAllTextAsync(paths.SelectedTestArtifacts, "null");
                break;
            case "null-artifact":
                await File.WriteAllTextAsync(paths.SelectedTestArtifacts, "[null]");
                break;
            case "missing-job":
                selected[0]!.AsObject().Remove("job");
                break;
            case "unknown-job":
                selected[0]!["job"] = "Missing job";
                break;
            case "duplicate-job":
                await File.WriteAllTextAsync(paths.FailedJobs, """[{"id":1,"name":"Tests"},{"id":2,"name":"Tests"}]""");
                break;
            case "job-id":
                await File.WriteAllTextAsync(paths.FailedJobs, """[{"name":"Tests"}]""");
                break;
            case "artifact-id":
                selected[0]!.AsObject().Remove("id");
                break;
            case "format":
                selected[0]!.AsObject().Remove("format");
                break;
            case "missing-directory":
                File.Delete(resultFile);
                Directory.Delete(results);
                break;
            case "missing-trx":
                File.Delete(resultFile);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        if (scenario is "missing-job" or "unknown-job" or "artifact-id" or "format")
        {
            await File.WriteAllTextAsync(paths.SelectedTestArtifacts, selected.ToJsonString());
        }

        var result = await RunPreparationAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(diagnostic, result.Output, StringComparison.Ordinal);
        Assert.Equal("unavailable", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(paths.RootDirectory, "test-evidence.json")))!["state"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, "test-failures.json")));
    }

    [Theory]
    [InlineData("failed-jobs.json", "[null]")]
    [InlineData("failed-jobs.json", "[false]")]
    [InlineData("selected-test-result-artifacts.json", "[null]")]
    [InlineData("selected-test-result-artifacts.json", "[false]")]
    public async Task VersionedEvidenceRejectsInvalidJobAndProvenanceArrayEntries(string fileName, string json)
    {
        await WriteGroupedFixtureAsync();
        var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        var document = JsonNode.Parse(AnalyzeCiFailure.TransientJson.Write(evidence))!;
        if (fileName == "failed-jobs.json")
        {
            document["jobs"] = JsonNode.Parse(json);
        }
        else
        {
            document["test_evidence"]!["failures"]![0]!["sources"] = JsonNode.Parse(json);
        }
        var result = await RunStrictValidationAsync(document.ToJsonString(), AnalyzeCiFailure.TransientJson.Write(proposal));
        Assert.Equal(1, result.ExitCode);
        Assert.Single(GetWorkflowCommandLines(result.Output));
        Assert.False(Directory.Exists(Path.Combine(_workspace.Path, "memory-repo")));
    }

    [Theory]
    [InlineData("""{"id":"stored-cause","type":"unrecognized","title":"Stored cause","custom":{"keep":true}}""")]
    [InlineData("""{"id":"stored-cause","type":"infra-failure","occurrences":[null]}""")]
    [InlineData("""{"id":"stored-cause","type":"flaky-test","tests":[null]}""")]
    public async Task PriorContextReportsInvalidOwnedSchemaWithoutRewritingOriginal(string json)
    {
        var results = await WritePreparationFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(results, "00001.trx"), "<TestRun><Results/></TestRun>");
        var paths = new AnalyzeCiFailure.EvidencePaths(EvidenceDirectory);
        Directory.CreateDirectory(paths.PriorCausesDirectory);
        var file = paths.PriorCause("stored-cause.json");
        await File.WriteAllTextAsync(file, json);

        var result = await RunPreparationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(paths.PriorCauseIndex))!.AsArray();
        var entry = Assert.Single(index)!;
        Assert.StartsWith("Cannot summarize prior cause record: ", entry["read_error"]?.GetValue<string>());
        Assert.Null(entry["id"]);
        Assert.Equal(json, await File.ReadAllTextAsync(file));
    }

    private async Task<CommandResult> RunPreparationAsync()
    {
        var paths = new AnalyzeCiFailure.EvidencePaths(EvidenceDirectory);
        var state = AnalyzeCiFailure.CiFailureJson.ReadContract<AnalyzeCiFailureFixtures.TestEvidenceState>(Path.Combine(paths.RootDirectory, "test-evidence.json"))?.State
            ?? AnalyzeCiFailure.EvidenceState.Unavailable;
        System.Collections.Immutable.ImmutableArray<AnalyzeCiFailure.CollectedTestFailure> failures = [];
        var basis = _preparationEvidence ?? throw new InvalidOperationException("Create a preparation fixture before parsing results.");
        var jobs = basis.Jobs;
        string diagnostic = "";
        if (state == AnalyzeCiFailure.EvidenceState.Complete)
        {
            try
            {
                var jobData = (JsonArray)AnalyzeCiFailure.CiFailureJson.Read(paths.FailedJobs)!;
                AnalyzeCiFailureFixtures.AddFixtureStepNumbers(jobData);
                jobs = AnalyzeCiFailure.CollectionProjection.ProjectJobs(jobData);
                var selected = AnalyzeCiFailure.CiFailureJson.ReadContract<System.Collections.Immutable.ImmutableArray<AnalyzeCiFailure.TestResultArtifactRecord>>(paths.SelectedTestArtifacts);
                var artifacts = selected.Select(artifact => new AnalyzeCiFailure.SelectedTestArtifact(
                    new AnalyzeCiFailure.WorkflowArtifact(JsonNode.Parse(artifact.Id!.Value.GetRawText()), artifact.Name, SizeInBytes: null, Expired: false, CreatedAt: null),
                    new AnalyzeCiFailure.TestArtifactExpectation(
                        artifact.Job!,
                        artifact.Name!,
                        artifact.Format!.Value,
                        artifact.Format == AnalyzeCiFailure.TestArtifactFormat.Trx
                            ? AnalyzeCiFailure.TestEvidencePolicy.Required : AnalyzeCiFailure.TestEvidencePolicy.OptionalDiagnostics))).ToArray();
                failures = AnalyzeCiFailure.TestFailureReader.ConsolidateFailures(AnalyzeCiFailure.TestFailureReader.Read(Path.Combine(paths.RootDirectory, "test-results"), jobs, artifacts));
                var projection = AnalyzeCiFailure.PublicationEvidenceProjection.ProjectTestFailures(basis with
                {
                    Jobs = jobs,
                    TestEvidence = basis.TestEvidence with { State = state, Failures = failures },
                });
                AnalyzeCiFailure.CiFailureJson.WriteContract(Path.Combine(paths.RootDirectory, "test-failures.json"), projection);
            }
            catch (Exception ex) when (ex is AnalyzeCiFailure.ValidationException or IOException or JsonException or InvalidOperationException or NullReferenceException or InvalidCastException)
            {
                state = AnalyzeCiFailure.EvidenceState.Unavailable;
                File.Delete(Path.Combine(paths.RootDirectory, "test-failures.json"));
                diagnostic = "::error::" + AnalyzeCiFailure.CiFailureJson.Display(ex.Message);
            }
        }
        AnalyzeCiFailure.CiFailureJson.WriteContract(Path.Combine(paths.RootDirectory, "test-evidence.json"), new AnalyzeCiFailureFixtures.TestEvidenceState(state));
        var attachments = Directory.GetFiles(paths.RootDirectory, "*", SearchOption.AllDirectories)
            .Where(file => file.StartsWith(paths.PriorCausesDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) || file.EndsWith(".log", StringComparison.Ordinal))
            .ToImmutableDictionary(
                file => Path.GetRelativePath(paths.RootDirectory, file).Replace(Path.DirectorySeparatorChar, '/'),
                file => new AnalyzeCiFailure.CollectedAttachment(File.ReadAllBytes(file).ToImmutableArray()));
        var catalogue = AnalyzeCiFailure.PriorCauseCatalogue.BuildIndex(attachments, failures);
        AnalyzeCiFailure.CiFailureJson.WriteContract(paths.PriorCauseIndex, catalogue);
        var bundle = basis with { Jobs = jobs, TestEvidence = basis.TestEvidence with { State = state, Failures = failures } };
        var input = AnalyzeCiFailure.WorkflowFailureDiagnosisContextBuilder.Build(new(bundle, attachments, catalogue));
        await File.WriteAllTextAsync(paths.Bundle, AnalyzeCiFailure.TransientJson.Write(bundle));
        await File.WriteAllTextAsync(paths.Summary, input.Summary);
        return new(state == AnalyzeCiFailure.EvidenceState.Unavailable ? 1 : 0, diagnostic);
    }

    private async Task<string> WritePreparationFixtureAsync(string format = "trx", string job = "Tests")
    {
        await WriteGroupedFixtureAsync();
        _preparationEvidence = AnalyzeCiFailureFixtures.Read(_workspace.Path).Evidence;
        var data = EvidenceDirectory;
        await File.WriteAllTextAsync(
            Path.Combine(data, "diagnostics/selected-test-result-artifacts.json"),
            JsonSerializer.Serialize(new[] { new { id = 8, name = "selected-results", job, format } }));
        return Directory.CreateDirectory(Path.Combine(data, "test-results", "8")).FullName;
    }

    private async Task<CommandResult> RunResultPreparationAsync(
        string resultsDirectory,
        string job,
        string jobsPath,
        string outputPath,
        string format = "trx")
    {
        var selected = await WritePreparationFixtureAsync(format, job);
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "diagnostics/failed-jobs.json"),
            await File.ReadAllTextAsync(jobsPath));
        foreach (var file in Directory.GetFiles(resultsDirectory))
        {
            File.Copy(file, Path.Combine(selected, Path.GetFileName(file)));
        }
        var result = await RunPreparationAsync();
        var prepared = Path.Combine(EvidenceDirectory, "test-failures.json");
        if (File.Exists(prepared))
        {
            File.Copy(prepared, outputPath);
        }
        return result;
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task PreparationPreservesHistoricalStructuralIdentityAndRedactsDescriptions()
    {
        var results = await WritePreparationFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(results, "00001.trx"), "<TestRun><Results/></TestRun>");
        var data = EvidenceDirectory;
        var prior = Directory.CreateDirectory(Path.Combine(data, "history/causes")).FullName;
        var ids = new[]
        {
            "typescript-sdk-nuxt-integration-npm-ci-docker-build-failure",
            "pack-cli-archive-npm-install-cannot-find-module-aspire-cli",
            "long-" + new string('a', count: 190),
        };
        foreach (var id in ids)
        {
            await File.WriteAllTextAsync(
                Path.Combine(prior, id + ".json"),
                JsonSerializer.Serialize(new
                {
                    id,
                    type = "infra-failure",
                    title = "Failure token=history-secret",
                    error_pattern = "Request password=history-secret",
                    occurrences = Array.Empty<object>(),
                }));
        }

        var result = await RunPreparationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "history/cause-index.json")))!.AsArray();
        Assert.Equal(ids.Order(StringComparer.Ordinal), index.Select(item => item!["id"]!.GetValue<string>()));
        foreach (var item in index)
        {
            var id = item!["id"]!.GetValue<string>();
            Assert.Equal("history/causes/" + id + ".json", item["file"]!.GetValue<string>());
            var original = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, item["file"]!.GetValue<string>())))!;
            Assert.Equal(id, original["id"]!.GetValue<string>());
            Assert.Equal("Failure token=[REDACTED]", item["title"]!.GetValue<string>());
            Assert.Equal("Request password=[REDACTED]", item["error_pattern"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("Invalid_ID")]
    [InlineData("different-valid-slug")]
    [RequiresTools(["bash", "jq"])]
    public async Task PreparationDoesNotExposeInvalidHistoricalIdAsAReusableCause(string id)
    {
        var results = await WritePreparationFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(results, "00001.trx"), "<TestRun><Results/></TestRun>");
        var data = EvidenceDirectory;
        var prior = Directory.CreateDirectory(Path.Combine(data, "history/causes")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(prior, "stored-cause.json"),
            JsonSerializer.Serialize(new { id, type = "infra-failure", title = "Stored cause" }));

        var result = await RunPreparationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var entry = Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "history/cause-index.json")))!.AsArray())!;
        Assert.Equal("history/causes/stored-cause.json", entry["file"]!.GetValue<string>());
        Assert.Null(entry["id"]);
        Assert.Equal("Cannot summarize prior cause record: Prior cause ID does not match its filename", entry["read_error"]!.GetValue<string>());
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task PreparationExposesLateKeylessAndOpaquePriorRecordsBeyondOldSummaryCutoff()
    {
        var results = await WritePreparationFixtureAsync();
        await File.WriteAllTextAsync(
            Path.Combine(results, "00001.trx"),
            """<TestRun><Results><UnitTestResult testName="Tests.Method(value: 1/2)" outcome="Failed"><Output><ErrorInfo><Message>Connection timed out</Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>""");
        var data = EvidenceDirectory;
        await File.WriteAllTextAsync(Path.Combine(data, "diagnostics/job-123.log"), "Download failed: HTTP 503 from package feed");
        var prior = Directory.CreateDirectory(Path.Combine(data, "history/causes")).FullName;
        for (var i = 0; i < 120; i++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(prior, $"cause-{i:D3}.json"),
                JsonSerializer.Serialize(new
                {
                    id = $"cause-{i:D3}",
                    type = "flaky-test",
                    title = new string('t', count: 238),
                    test_name = $"Unrelated.Test{i}",
                    error_pattern = new string('e', count: 500),
                }));
        }
        var lateCause = """
            {"id":"z-late","type":"flaky-test","title":"Debugger connection timeout","error_pattern":"Connection (?:timed out|closed)","issue_url":"https://github.com/microsoft/aspire/issues/1","occurrences":[{"run_id":1,"tests":[{"name":"Tests.Method(value: 1/2)","job_id":999,"error":"Detailed diagnostic only in full history"}]}]}
            """;
        await File.WriteAllTextAsync(Path.Combine(prior, "z-late.json"), lateCause);
        await File.WriteAllTextAsync(
            Path.Combine(prior, "z-keyless.json"),
            """{"id":"z-keyless","type":"infra-failure","title":"Package feed unavailable","error_pattern":"HTTP 503","occurrences":[]}""");
        await File.WriteAllTextAsync(
            Path.Combine(prior, "z-opaque.json"),
            """{"id":"z-opaque","type":"flaky-test","title":"Legacy group","test_name":"Tests.First / Tests.Second(value: a/b)","error_pattern":"legacy","occurrences":[]}""");
        await File.WriteAllTextAsync(
            Path.Combine(prior, "z-other-mechanism.json"),
            """{"id":"z-other-mechanism","type":"flaky-test","title":"Different assertion in the same test","error_pattern":"Expected true","occurrences":[{"run_id":2,"tests":[{"name":"Tests.Method(value: 1/2)","job_id":1000}]}]}""");

        var result = await RunPreparationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "history/cause-index.json")))!.AsArray();
        Assert.Equal(124, index.Count);
        Assert.True(new FileInfo(Path.Combine(data, "history/cause-index.json")).Length > 65536);
        var late = Assert.Single(index, cause => cause!["id"]!.GetValue<string>() == "z-late")!;
        Assert.True(late["current_test_match"]!.GetValue<bool>());
        Assert.Equal("history/causes/z-late.json", late["file"]!.GetValue<string>());
        Assert.Equal(lateCause, await File.ReadAllTextAsync(Path.Combine(data, late["file"]!.GetValue<string>())));
        var keyless = Assert.Single(index, cause => cause!["id"]!.GetValue<string>() == "z-keyless")!;
        Assert.True(keyless["current_diagnostic_match"]!.GetValue<bool>());
        Assert.Equal(
            "Tests.First / Tests.Second(value: a/b)",
            Assert.Single(index, cause => cause!["id"]!.GetValue<string>() == "z-opaque")!["test_name"]!.GetValue<string>());
        var summary = await File.ReadAllTextAsync(Path.Combine(data, "analysis-summary.md"));
        Assert.InRange(summary.IndexOf("z-late", StringComparison.Ordinal), 0, 10000);
        Assert.InRange(summary.IndexOf("z-other-mechanism", StringComparison.Ordinal), 0, 10000);
        Assert.InRange(summary.IndexOf("z-keyless", StringComparison.Ordinal), 0, 10000);
        Assert.Contains("history/cause-index.json", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"")]
    [RequiresTools(["bash", "jq"])]
    public async Task PreparationKeepsExactSelectedTrxIdentityStderrAndProvenance(string xmlNamespace)
    {
        var directory = await WritePreparationFixtureAsync();
        await File.WriteAllTextAsync(
            Path.Combine(directory, "00001.trx"),
            $$"""
            <TestRun{{xmlNamespace}}>
              <TestDefinitions>
                <UnitTest id="same"><TestMethod className="Canonical.Type" name="Method" /></UnitTest>
                <UnitTest id="same"><TestMethod className="Wrong.Type" name="Other" /></UnitTest>
              </TestDefinitions>
              <Results>
                <UnitTestResult testId="same" testName="Tests.Method(value: &quot;one/two&quot;)" outcome="Failed">
                  <Output><ErrorInfo><Message>Connection timed out</Message><StackTrace>frame</StackTrace></ErrorInfo><StdOut>stdout</StdOut><StdErr>stderr</StdErr></Output>
                </UnitTestResult>
                <UnitTestResult testName="Not selected outcome" outcome="Error" />
              </Results>
            </TestRun>
            """);
        var unselected = Directory.CreateDirectory(Path.Combine(EvidenceDirectory, "test-results", "999")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(unselected, "00001.trx"),
            """<TestRun><Results><UnitTestResult testName="Forged" outcome="Failed" /></Results></TestRun>""");

        var result = await RunPreparationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var failures = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "test-failures.json")))!;
        await Verifier.Verify(failures.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json")
            .UseDirectory("Snapshots").UseParameters(xmlNamespace.Length == 0 ? "plain" : "namespaced");
    }

    [Theory]
    [InlineData("dtd")]
    [InlineData("external-entity")]
    [InlineData("malformed")]
    [InlineData("invalid-schema")]
    [InlineData("wrong-root")]
    [InlineData("missing")]
    [RequiresTools(["bash", "jq"])]
    public async Task PreparationRejectsInvalidRequiredTrxWithoutPartialEvidence(string kind)
    {
        var directory = await WritePreparationFixtureAsync();
        await File.WriteAllTextAsync(
            Path.Combine(directory, "00001.trx"),
            """<TestRun><Results><UnitTestResult testName="Valid first" outcome="Failed" /></Results></TestRun>""");
        var secret = Path.Combine(_workspace.Path, "external.xml");
        await File.WriteAllTextAsync(secret, "External data must not be read");
        if (kind != "missing")
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "00002.trx"),
                kind switch
                {
                    "dtd" => "<!DOCTYPE TestRun [<!ELEMENT TestRun ANY>]><TestRun />",
                    "external-entity" => $"""<!DOCTYPE TestRun [<!ENTITY external SYSTEM "{new Uri(secret).AbsoluteUri}">]><TestRun><Results>&external;</Results></TestRun>""",
                    "malformed" => "<TestRun><",
                    "invalid-schema" => """<TestRun><ResultSummary><Counters total="not-a-number" /></ResultSummary></TestRun>""",
                    _ => "<NotATrx />",
                });
        }
        else
        {
            File.Delete(Path.Combine(directory, "00001.trx"));
        }

        var result = await RunPreparationAsync();

        Assert.Equal(1, result.ExitCode);
        var data = EvidenceDirectory;
        Assert.False(File.Exists(Path.Combine(data, "test-failures.json")));
        Assert.Equal("unavailable", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(data, "test-evidence.json")))!["state"]!.GetValue<string>());
        Assert.Contains("Some required test evidence is unavailable", await File.ReadAllTextAsync(Path.Combine(data, "analysis-summary.md")), StringComparison.Ordinal);
        Assert.Contains(
            kind == "missing" ? "no '*.trx' files were found" : "Cannot parse test result 'extracted-results/8/00002.trx' from artifact 8:",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task PreparationDeduplicatesIdenticalFailuresAcrossFilesBeforeValidation()
    {
        var directory = await WritePreparationFixtureAsync();
        const string trx = """<TestRun><Results><UnitTestResult executionId="first-execution" testName="Tests.Method(value: 1/2)" outcome="Failed"><Output><ErrorInfo><Message>timeout</Message></ErrorInfo></Output></UnitTestResult><UnitTestResult executionId="second-execution" testName="Tests.Other" outcome="Failed"><Output><ErrorInfo><Message>timeout</Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>""";
        await File.WriteAllTextAsync(Path.Combine(directory, "00001.trx"), trx);
        await File.WriteAllTextAsync(Path.Combine(directory, "00002.trx"), trx);

        var prepared = await RunPreparationAsync();
        Assert.True(prepared.ExitCode == 0, prepared.Output);
        var validated = await RunValidationScriptAsync("");
        Assert.True(validated.ExitCode == 0, validated.Output);
        var failures = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "test-failures.json")))!.AsArray();
        Assert.Equal(2, failures.Count);
        Assert.All(failures, failure => Assert.Equal("00001.trx", failure!["result_file"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task PreparationAndPublicationPreserveDistinctFrameworkExecutions(bool identicalDiagnostics, bool executionIds)
    {
        const string diagnosticSuffix = "\r\n\r\n\t@reviewers [details](https://example.invalid/details)\n![image](https://example.invalid/image)\n\n<div>Generic<T>\u200b\u007f</div>\r\n```markdown\n# heading\n```\nmultiline diagnostic";
        var failures = await WriteFrameworkObservationsAsync(identicalDiagnostics, executionIds, diagnosticSuffix);
        Assert.Equal(2, failures.Count);
        Assert.Equal(
            new[] { "observation-1", "observation-2" },
            failures.Select(test => test!["observation_id"]!.GetValue<string>()));
        Assert.Equal(
            new[] { "00001.trx", "00002.trx" },
            failures.Select(test => test!["result_file"]!.GetValue<string>()));

        var result = await RunSuccessfulGroupedPublicationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var history = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "causes", "debugger-timeout.json")))!;
        var tests = history["occurrences"]![0]!["tests"]!.AsArray();
        Assert.Equal(2, tests.Count);
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "runs", "123.json")))!;
        var body = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
        for (var i = 0; i < tests.Count; i++)
        {
            foreach (var field in new[] { "error", "stack_trace", "standard_output", "standard_error", "result_file", "observation_id" })
            {
                Assert.Equal(failures[i]![field]!.GetValue<string>(), tests[i]![field]!.GetValue<string>());
                Assert.Equal(failures[i]![field]!.GetValue<string>(), summary["failed_tests"]![i]![field]!.GetValue<string>());
                var value = failures[i]![field]!.GetValue<string>();
                Assert.Contains(
                    field is "error" or "stack_trace" or "standard_output" or "standard_error"
                    ? $"\n\n    {field}:\n\n        " + value.Replace("\n", "\n        ", StringComparison.Ordinal)
                    : value,
                    body,
                    StringComparison.Ordinal);
            }
            Assert.Equal(executionIds ? $"execution-net{10 + i}" : null, tests[i]!["execution_id"]?.GetValue<string>());
            if (executionIds)
            {
                Assert.Contains($"execution-net{10 + i}", body, StringComparison.Ordinal);
            }
        }
        await Verifier.Verify(
            new JsonObject
            {
                ["observations"] = tests.DeepClone(),
                ["issue_body"] = body,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            "json")
            .UseDirectory("Snapshots").UseParameters(identicalDiagnostics, executionIds);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("swapped-name")]
    [InlineData("swapped-job")]
    [InlineData("cause-missing")]
    [InlineData("cause-duplicate")]
    [InlineData("cause-unknown")]
    [InlineData("cause-swapped-name")]
    [InlineData("cause-swapped-job")]
    [InlineData("legacy-cause")]
    [RequiresTools(["bash", "jq"])]
    public async Task FrameworkObservationsRejectInvalidReferences(string kind)
    {
        await WriteFrameworkObservationsAsync(identicalDiagnostics: true, executionIds: true, diagnosticSuffix: "");
        var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        var document = JsonNode.Parse(AnalyzeCiFailure.TransientJson.Write(proposal))!;
        var causeReference = kind.StartsWith("cause-", StringComparison.Ordinal) || kind == "legacy-cause";
        var cause = document["causes"]![0]!;
        var tests = causeReference ? cause["test_refs"]!.AsArray() : document["tests"]!.AsArray();
        if (kind == "legacy-cause")
        {
            cause.AsObject().Remove("test_refs");
            cause["test_name"] = "Tests.Method(value: 1/2)";
        }
        else if (kind.EndsWith("missing", StringComparison.Ordinal))
        {
            if (causeReference)
            {
                cause.AsObject().Remove("test_refs");
            }
            else
            {
                tests[0]!.AsObject().Remove("ref");
            }
        }
        else if (kind.EndsWith("duplicate", StringComparison.Ordinal))
        {
            if (causeReference)
            {
                tests[1] = tests[0]!.DeepClone();
            }
            else
            {
                tests[1]!["ref"] = tests[0]!["ref"]!.DeepClone();
            }
        }
        else if (kind.EndsWith("swapped-name", StringComparison.Ordinal))
        {
            if (causeReference)
            {
                tests[0] = "Tests.Other";
            }
            else
            {
                tests[0]!["name"] = "Tests.Other";
            }
        }
        else if (kind.EndsWith("swapped-job", StringComparison.Ordinal))
        {
            if (causeReference)
            {
                cause["job_ids"]![0] = "124";
            }
            else
            {
                tests[0]!["job_id"] = "124";
            }
        }
        else
        {
            if (causeReference)
            {
                tests[0] = "unknown-ref";
            }
            else
            {
                tests[0]!["ref"] = "unknown-ref";
            }
        }
        var result = await RunStrictValidationAsync(AnalyzeCiFailure.TransientJson.Write(evidence), document.ToJsonString());
        Assert.Equal(1, result.ExitCode);
        Assert.Single(GetWorkflowCommandLines(result.Output));
        Assert.False(Directory.Exists(Path.Combine(_workspace.Path, "memory-repo")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [RequiresTools(["bash", "jq"])]
    public async Task CauseReferenceCoverageIsScopedToItsClaimedJobs(bool claimAmbiguousJob, bool sharedJobName)
    {
        await WriteFrameworkObservationsAsync(identicalDiagnostics: true, executionIds: true, diagnosticSuffix: "");
        var data = EvidenceDirectory;
        var jobsPath = Path.Combine(data, "diagnostics/failed-jobs.json");
        var jobs = JsonNode.Parse(await File.ReadAllTextAsync(jobsPath))!.AsArray();
        var jobName = sharedJobName ? "Tests" : "Z Legacy Tests";
        jobs.Add(new JsonObject { ["id"] = 124, ["name"] = jobName });
        await File.WriteAllTextAsync(jobsPath, jobs.ToJsonString());
        var trustedPath = Path.Combine(data, "test-failures.json");
        var collected = JsonNode.Parse(await File.ReadAllTextAsync(trustedPath))!.AsArray();
        collected.Add(new JsonObject { ["test"] = "Tests.Method(value: 1/2)", ["job"] = jobName, ["job_id"] = 124, ["error"] = "Legacy timeout" });
        await File.WriteAllTextAsync(trustedPath, collected.ToJsonString());
        var directory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var analysisPath = Path.Combine(directory, "analysis-result.json");
        var analysis = JsonNode.Parse(await File.ReadAllTextAsync(analysisPath))!;
        analysis["failed_jobs"]!.AsArray().Add(new JsonObject { ["id"] = 124, ["classification"] = "flaky-test" });
        analysis["failed_tests"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "Tests.Method(value: 1/2)",
            ["job"] = jobName,
            ["job_id"] = 124,
            ["error"] = "",
            ["classification"] = "flaky",
            ["reason"] = "Legacy timeout",
        });
        analysis["causes"]!.AsArray().Add("legacy-timeout");
        await File.WriteAllTextAsync(analysisPath, analysis.ToJsonString());
        await File.WriteAllTextAsync(
            Path.Combine(directory, "causes", "legacy-timeout.json"),
            $$"""{"id":"legacy-timeout","type":"flaky-test","title":"Legacy timeout","test_name":"Tests.Method(value: 1/2)","error_pattern":"timeout","job_ids":{{(claimAmbiguousJob ? "[123,124]" : "[124]")}}}""");

        var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        var causes = proposal.Causes.Select(cause => cause is AnalyzeCiFailure.NewCauseProposal created && created.Definition.Id == "legacy-timeout"
            ? created with { TestRefs = evidence.TestEvidence.Failures.Where(test => test.JobId == 124).Select(test => test.Ref).ToImmutableArray() }
            : cause).ToImmutableArray();
        var result = await RunStrictValidationAsync(
            AnalyzeCiFailure.TransientJson.Write(evidence),
            AnalyzeCiFailure.TransientJson.Write(proposal with { Causes = causes }));
        Assert.Equal(claimAmbiguousJob ? 1 : 0, result.ExitCode);
        if (claimAmbiguousJob)
        {
            Assert.Contains("Every cause job must contain a referenced test observation", result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task PreparationScopesExecutionIdentityToProducingJob()
    {
        var directory = await WritePreparationFixtureAsync();
        var trx = $"<TestRun><Results>{FrameworkTrx("same-execution", "timeout")}</Results></TestRun>";
        await File.WriteAllTextAsync(Path.Combine(directory, "00001.trx"), trx);
        var second = Directory.CreateDirectory(Path.Combine(EvidenceDirectory, "test-results", "9")).FullName;
        await File.WriteAllTextAsync(Path.Combine(second, "00001.trx"), trx);
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "diagnostics/failed-jobs.json"),
            """[{"id":123,"name":"Tests"},{"id":124,"name":"Other Tests"}]""");
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "diagnostics/selected-test-result-artifacts.json"),
            """[{"id":8,"job":"Tests","format":"trx"},{"id":9,"job":"Other Tests","format":"trx"}]""");

        var result = await RunPreparationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var failures = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "test-failures.json")))!.AsArray();
        Assert.Equal(new[] { 123, 124 }, failures.Select(test => test!["job_id"]!.GetValue<int>()).Order());
        Assert.All(
            failures,
            test =>
        {
            Assert.Equal("same-execution", test!["execution_id"]!.GetValue<string>());
            Assert.Null(test["observation_id"]);
        });
    }

    [Theory]
    [InlineData("diagnostics")]
    [InlineData("name")]
    [RequiresTools(["bash", "jq"])]
    public async Task PreparationRejectsConflictingCopiesOfTheSameExecution(string conflict)
    {
        var directory = await WritePreparationFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "00001.trx"), $"<TestRun><Results>{FrameworkTrx("same-execution", "first")}</Results></TestRun>");
        var second = conflict == "diagnostics" ? FrameworkTrx("same-execution", "second") :
            FrameworkTrx("same-execution", "first").Replace("Tests.Method(value: 1/2)", "Tests.Other", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(directory, "00002.trx"), $"<TestRun><Results>{second}</Results></TestRun>");

        var result = await RunPreparationAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Conflicting diagnostics for the same test execution", result.Output, StringComparison.Ordinal);
        Assert.Equal("unavailable", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "test-evidence.json")))!["state"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(EvidenceDirectory, "test-failures.json")));
    }

    private static string FrameworkTrx(string? execution, string diagnostic)
    {
        var escaped = System.Security.SecurityElement.Escape(diagnostic);
        return $"""<UnitTestResult testName="Tests.Method(value: 1/2)" outcome="Failed" {(execution is null ? "" : $"executionId=\"{execution}\"")}><Output><ErrorInfo><Message>{escaped}</Message><StackTrace>frame {escaped}</StackTrace></ErrorInfo><StdOut>stdout {escaped}</StdOut><StdErr>stderr {escaped}</StdErr></Output></UnitTestResult>""";
    }

    private async Task<JsonArray> WriteFrameworkObservationsAsync(bool identicalDiagnostics, bool executionIds, string diagnosticSuffix)
    {
        var directory = await WritePreparationFixtureAsync();
        var prefix = diagnosticSuffix.Length > 0 ? "\r\n\r\n\t" : "";
        var first = FrameworkTrx(executionIds ? "execution-net10" : null, prefix + "net10 timeout" + diagnosticSuffix);
        var second = FrameworkTrx(executionIds ? "execution-net11" : null, prefix + (identicalDiagnostics ? "net10 timeout" : "net11 timeout") + diagnosticSuffix);
        await File.WriteAllTextAsync(Path.Combine(directory, "00001.trx"), $"<TestRun><Results>{first}</Results></TestRun>");
        await File.WriteAllTextAsync(Path.Combine(directory, "00002.trx"), $"<TestRun><Results>{second}</Results></TestRun>");
        if (executionIds)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "00003.trx"), $"<TestRun><Results>{first}{second}</Results></TestRun>");
        }
        var prepared = await RunPreparationAsync();
        Assert.True(prepared.ExitCode == 0, prepared.Output);
        var failures = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "test-failures.json")))!.AsArray();
        var analysisDirectory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var summaryPath = Path.Combine(analysisDirectory, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        summary["failed_tests"] = new JsonArray(failures.Select(test => (JsonNode)new JsonObject
        {
            ["name"] = test!["test"]!.DeepClone(),
            ["job"] = test["job"]!.DeepClone(),
            ["job_id"] = test["job_id"]!.DeepClone(),
            ["observation_id"] = test["observation_id"]?.DeepClone(),
            ["error"] = "Untrusted diagnostic",
            ["classification"] = "flaky",
            ["reason"] = "Framework execution timeout",
        }).ToArray());
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        var causePath = Path.Combine(analysisDirectory, "causes", "debugger-timeout.json");
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(causePath))!;
        cause["tests"] = new JsonArray(failures.Select(test => (JsonNode)new JsonObject
        {
            ["name"] = test!["test"]!.DeepClone(),
            ["job_id"] = test["job_id"]!.DeepClone(),
            ["observation_id"] = test["observation_id"]?.DeepClone(),
        }).ToArray());
        await File.WriteAllTextAsync(causePath, cause.ToJsonString());
        return failures;
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task DecisionReferencesPreserveRawNamesAndPublishOnlyTheirRedactedDisplay()
    {
        var directory = await WritePreparationFixtureAsync("mocha");
        const string name = "Tests.Method(token: \"fake-value-for-redaction\")";
        await File.WriteAllTextAsync(
            Path.Combine(directory, "00001.json"),
            JsonSerializer.Serialize(new
            {
                tests = new[] { new { fullTitle = name } },
                failures = new[] { new { fullTitle = name, err = new { name = "AssertionError", message = "Trusted assertion", stack = "frame" } } }
            }));
        var analysisDirectory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var summaryPath = Path.Combine(analysisDirectory, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        summary["failed_tests"] = new JsonArray(new JsonObject
        {
            ["name"] = name,
            ["job"] = "Tests",
            ["error"] = "Agent diagnostic must not survive",
            ["classification"] = "flaky",
            ["reason"] = "Intermittent assertion",
        });
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        var causePath = Path.Combine(analysisDirectory, "causes", "debugger-timeout.json");
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(causePath))!;
        cause.AsObject().Remove("tests");
        cause["test_name"] = name;
        await File.WriteAllTextAsync(causePath, cause.ToJsonString());

        var prepared = await RunPreparationAsync();
        Assert.True(prepared.ExitCode == 0, prepared.Output);
        var validated = await RunValidationScriptAsync("");
        Assert.True(validated.ExitCode == 0, validated.Output);
        summary = AnalyzeCiFailure.CiFailureJson.ToNode(AnalyzeCiFailureFixtures.Validate(_workspace.Path).Report)!;
        Assert.Equal("Trusted assertion", summary["failed_tests"]![0]!["error"]!.GetValue<string>());
        Assert.Equal(name, summary["failed_tests"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("Tests.Method(token:[REDACTED]", summary["failed_tests"]![0]!["display_name"]!.GetValue<string>());
        var published = await RunSuccessfulGroupedPublicationAsync();
        Assert.True(published.ExitCode == 0, published.Output);
        var history = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "causes", "debugger-timeout.json")))!;
        Assert.Equal("Tests.Method(token:[REDACTED]", history["occurrences"]![0]!["tests"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(
            new[] { "error_pattern", "id", "issue_url", "occurrences", "title", "type" },
            history.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
    }
}
