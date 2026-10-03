// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class TestSelectionAuditWorkflowTests
{
    private static readonly string s_workflowsPath = Path.Combine(RepoRoot.Path, ".github", "workflows");
    private readonly ITestOutputHelper _testOutput;

    public TestSelectionAuditWorkflowTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public void TestSelectionAuditCapsAiCreditUsage()
    {
        var root = LoadWorkflow("test-selection-audit.md");

        Assert.Equal("300", Scalar(root, "max-ai-credits"));
        Assert.Equal("600", Scalar(root, "max-daily-ai-credits"));
    }

    [Fact]
    public void TestSelectionAuditBlocksSafeOutputsWhenAgentFails()
    {
        const string guardName = "Require successful audit before safe outputs";
        const string guardCondition = "needs.agent.result != 'success'";

        var authoredRoot = LoadWorkflow("test-selection-audit.md");
        var authoredGuard = Step(authoredRoot, guardName);
        Assert.Equal(guardCondition, Scalar(authoredGuard, "if"));
        Assert.Contains("exit 1", Scalar(authoredGuard, "run"), StringComparison.Ordinal);

        var compiledRoot = LoadWorkflow("test-selection-audit.lock.yml");
        var safeOutputs = Mapping(Mapping(compiledRoot, "jobs"), "safe_outputs");
        var steps = Assert.IsType<YamlSequenceNode>(
            safeOutputs.Children[new YamlScalarNode("steps")]);
        var stepMappings = steps.Children.Select(Assert.IsType<YamlMappingNode>).ToList();
        var guard = Assert.Single(stepMappings, step => Scalar(step, "name") == guardName);
        var guardIndex = stepMappings.IndexOf(guard);
        var processorIndex = stepMappings.FindIndex(
            step => Scalar(step, "name") == "Process Safe Outputs");

        Assert.Equal(guardCondition, Scalar(guard, "if"));
        Assert.DoesNotContain(new YamlScalarNode("continue-on-error"), guard.Children.Keys);
        Assert.True(
            guardIndex >= 0 && processorIndex > guardIndex,
            "The successful-agent guard must run before safe-output processing.");
    }

    [Theory]
    [InlineData(".md")]
    [InlineData(".lock.yml")]
    public void TestSelectionAuditCollectsBoundedEvidenceBeforeTheAgentRuns(string extension)
    {
        var root = LoadWorkflow("test-selection-audit" + extension);
        var compactMemory = Step(root, "Compact test-selection memory");
        var collector = Step(root, "Collect test-selection evidence");
        var compactScript = ReadTestSelectionAuditFile("compact_memory.py");
        var collectorScript = ReadTestSelectionAuditFile("collect_evidence.py");

        Assert.Equal("/tmp/gh-aw/repo-memory/default", Scalar(Mapping(compactMemory, "env"), "MEMORY_ROOT"));
        Assert.Equal("14", Scalar(Mapping(compactMemory, "env"), "RETENTION_DAYS"));
        Assert.Equal("${{ runner.temp }}/gh-aw/test-selection-audit/audit-date.txt", Scalar(Mapping(compactMemory, "env"), "AUDIT_DATE_PATH"));
        Assert.Equal("python3 .github/workflows/test-selection-audit/compact_memory.py", Scalar(compactMemory, "run"));
        Assert.Contains("row[\"seen\"] >= cutoff", compactScript, StringComparison.Ordinal);
        Assert.Contains("row.get(\"verdict\") == \"watch\"", compactScript, StringComparison.Ordinal);
        Assert.Equal("${{ runner.temp }}/gh-aw/test-selection-audit/evidence.json", Scalar(Mapping(collector, "env"), "OUTPUT_PATH"));
        Assert.Equal("/tmp/gh-aw/repo-memory/default/processed-runs.jsonl", Scalar(Mapping(collector, "env"), "PROCESSED_RUNS_PATH"));
        Assert.Equal("${{ runner.temp }}/gh-aw/test-selection-audit/processed-runs-before.jsonl", Scalar(Mapping(collector, "env"), "PROCESSED_BASELINE_PATH"));
        Assert.Equal("/tmp/gh-aw/repo-memory/default/watchlist.jsonl", Scalar(Mapping(collector, "env"), "WATCHLIST_PATH"));
        Assert.Equal("${{ runner.temp }}/gh-aw/test-selection-audit/watchlist-before.jsonl", Scalar(Mapping(collector, "env"), "WATCHLIST_BASELINE_PATH"));
        Assert.Equal("${{ runner.temp }}/gh-aw/test-selection-audit/audit-date.txt", Scalar(Mapping(collector, "env"), "AUDIT_DATE_PATH"));
        Assert.Equal("python3 .github/workflows/test-selection-audit/collect_evidence.py", Scalar(collector, "run"));
        Assert.Contains("MAX_COMPRESSED_BYTES", collectorScript, StringComparison.Ordinal);
        Assert.Contains("MAX_EXPANDED_BYTES", collectorScript, StringComparison.Ordinal);
        Assert.Contains("http.client.IncompleteRead", collectorScript, StringComparison.Ordinal);
        Assert.Contains("for attempt in range(3)", collectorScript, StringComparison.Ordinal);
        Assert.Contains("ThreadPoolExecutor(max_workers=8)", collectorScript, StringComparison.Ordinal);
        Assert.Contains("entry.filename == ARTIFACT_MEMBER", collectorScript, StringComparison.Ordinal);
        Assert.Contains("stream.read(MAX_EXPANDED_BYTES + 1)", collectorScript, StringComparison.Ordinal);
        Assert.Contains("authorization_prefix + token", collectorScript, StringComparison.Ordinal);
        Assert.Contains("redirected.remove_header(\"Authorization\")", collectorScript, StringComparison.Ordinal);
        Assert.Contains("\"untrusted-fork-artifact\"", collectorScript, StringComparison.Ordinal);
        Assert.Contains("\"artifact-head-mismatch\"", collectorScript, StringComparison.Ordinal);
        Assert.Contains("\"selection-outside-lookback\"", collectorScript, StringComparison.Ordinal);
        Assert.Contains("\"pr-attribution-ambiguous\"", collectorScript, StringComparison.Ordinal);
        Assert.Contains("\"collector-error\"", collectorScript, StringComparison.Ordinal);
        Assert.Contains("\"recorded\"", collectorScript, StringComparison.Ordinal);
        Assert.Contains("len(numbers) > MAX_PRS", collectorScript, StringComparison.Ordinal);
        Assert.Contains("normalized[\"sourceHeadSha\"] == head_sha", collectorScript, StringComparison.Ordinal);
        Assert.Contains("except Exception as error:", collectorScript, StringComparison.Ordinal);
        Assert.Contains("\"sourceBaseSha\": source_base_sha", collectorScript, StringComparison.Ordinal);
        Assert.Contains("\"sourceHasDiff\": diff_match is not None", collectorScript, StringComparison.Ordinal);
        Assert.Contains("output_path.chmod(0o444)", collectorScript, StringComparison.Ordinal);

        if (extension == ".md")
        {
            Assert.Equal("30", Scalar(root, "timeout-minutes"));
            Assert.Equal("${{ github.run_id }}", Scalar(Mapping(root, "concurrency"), "job-discriminator"));
            var tools = Mapping(root, "tools");
            var bash = Assert.IsType<YamlSequenceNode>(tools.Children[new YamlScalarNode("bash")])
                .Children.Select(node => node.ToString()).ToArray();
            Assert.DoesNotContain("curl", bash);
            Assert.DoesNotContain("unzip", bash);

            var githubToolsets = Assert.IsType<YamlSequenceNode>(
                    Mapping(tools, "github").Children[new YamlScalarNode("toolsets")])
                .Children.Select(node => node.ToString()).ToArray();
            Assert.DoesNotContain("actions", githubToolsets);

            var repoMemory = Mapping(tools, "repo-memory");
            Assert.Equal([".jsonl"], Assert.IsType<YamlSequenceNode>(
                repoMemory.Children[new YamlScalarNode("allowed-extensions")]).Children.Select(node => node.ToString()));
            var validation = Scalar(Mapping(repoMemory, "validation"), "script");
            Assert.Contains("Invalid test-selection audit memory", validation, StringComparison.Ordinal);
            Assert.Contains("counter ${actualCount} does not match ${expectedCount}", validation, StringComparison.Ordinal);
            Assert.Contains("entry.name === \".git\"", validation, StringComparison.Ordinal);
            Assert.Contains("does not match trusted selection evidence", validation, StringComparison.Ordinal);
            Assert.Contains("is not an unchanged baseline or trusted selection", validation, StringComparison.Ordinal);
            Assert.Contains("changes a watch row without trusted current evidence", validation, StringComparison.Ordinal);
            Assert.Contains("requires selection-time diff attribution", validation, StringComparison.Ordinal);
            Assert.Contains("provenance files are incomplete", validation, StringComparison.Ordinal);
            Assert.Contains("must be a UTC date <=", validation, StringComparison.Ordinal);
        }
        else
        {
            var jobs = Mapping(root, "jobs");
            Assert.Equal(
                "gh-aw-copilot-${{ github.workflow }}-${{ github.run_id }}",
                Scalar(Mapping(Mapping(jobs, "agent"), "concurrency"), "group"));
            Assert.Equal(
                "gh-aw-conclusion-test-selection-audit-${{ github.run_id }}",
                Scalar(Mapping(Mapping(jobs, "conclusion"), "concurrency"), "group"));
            var mappings = Mappings(root).ToList();
            Assert.True(mappings.IndexOf(Step(root, "Clone repo-memory branch (default)")) < mappings.IndexOf(compactMemory));
            Assert.True(mappings.IndexOf(compactMemory) < mappings.IndexOf(collector));
            var compiledWorkflow = File.ReadAllText(Path.Combine(s_workflowsPath, "test-selection-audit.lock.yml"));
            var restoreBaseIndex = compiledWorkflow.IndexOf(
                "Restore agent config folders from base branch",
                StringComparison.Ordinal);
            var compactScriptIndex = compiledWorkflow.IndexOf(
                "python3 .github/workflows/test-selection-audit/compact_memory.py",
                StringComparison.Ordinal);
            Assert.True(
                restoreBaseIndex >= 0 && compactScriptIndex > restoreBaseIndex,
                "The trusted base .github tree must be restored before extracted scripts execute.");
            var validation = Step(root, "Validate repo-memory domain content (default)");
            Assert.NotEmpty(Scalar(Mapping(validation, "env"), "VALIDATION_SCRIPT_B64"));
            var agentRun = Scalar(Step(root, "Execute GitHub Copilot CLI"), "run");
            Assert.Contains("--mount \"${RUNNER_TEMP}/gh-aw:${RUNNER_TEMP}/gh-aw:ro\"", agentRun, StringComparison.Ordinal);
            Assert.DoesNotContain("--mount \"${RUNNER_TEMP}/gh-aw:${RUNNER_TEMP}/gh-aw:rw\"", agentRun, StringComparison.Ordinal);
            Assert.Contains("--mount \"${RUNNER_TEMP}/gh-aw:/host${RUNNER_TEMP}/gh-aw:ro\"", agentRun, StringComparison.Ordinal);
            Assert.DoesNotContain("--mount \"${RUNNER_TEMP}/gh-aw:/host${RUNNER_TEMP}/gh-aw:rw\"", agentRun, StringComparison.Ordinal);
        }
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task TestSelectionAuditRejectsMemoryCountersNotBackedByProcessedHeads()
    {
        var root = LoadWorkflow("test-selection-audit.md");
        var validationScript = Scalar(Mapping(Mapping(Mapping(root, "tools"), "repo-memory"), "validation"), "script");
        using var workspace = TemporaryWorkspace.Create(_testOutput);
        var memoryPath = Path.Combine(workspace.Path, "memory");
        Directory.CreateDirectory(memoryPath);
        Directory.CreateDirectory(Path.Combine(memoryPath, ".git"));

        var provenancePath = Path.Combine(workspace.Path, "gh-aw", "test-selection-audit");
        Directory.CreateDirectory(provenancePath);
        var evidencePath = Path.Combine(provenancePath, "evidence.json");
        var baselinePath = Path.Combine(provenancePath, "processed-runs-before.jsonl");
        var watchBaselinePath = Path.Combine(provenancePath, "watchlist-before.jsonl");
        var auditDatePath = Path.Combine(provenancePath, "audit-date.txt");
        var validationPath = Path.Combine(workspace.Path, "validation.js");
        await File.WriteAllTextAsync(validationPath, validationScript);
        var harnessPath = Path.Combine(workspace.Path, "validate-memory.js");
        await File.WriteAllTextAsync(
            harnessPath,
            """
            const fs = require("fs");
            const path = require("path");
            const vm = require("vm");
            const memoryRoot = process.argv[2];
            const script = fs.readFileSync(process.argv[3], "utf8");
            vm.runInNewContext(script, { fs, path, memoryRoot, process });
            """);

        var auditDay = DateTime.UtcNow.Date;
        var auditDate = auditDay.ToString("yyyy-MM-dd");
        var triggerPath = "src/caf\u00e9 file.cs";
        var processed = new Dictionary<string, object?>
        {
            ["pr"] = 42,
            ["sha"] = new string('a', 40),
            ["run"] = 100,
            ["attempt"] = 1,
            ["all"] = true,
            ["over_paths"] = new[] { triggerPath },
            ["miss_edges"] = Array.Empty<object>(),
            ["seen"] = auditDate
        };
        var watch = new Dictionary<string, object?>
        {
            ["path"] = triggerPath,
            ["rule"] = "src/**",
            ["rule_ref"] = "eng/github-ci/test-trigger-map.yml@abcdef1",
            ["path_ref"] = $"{triggerPath}@abcdef1",
            ["kind"] = "over-selection",
            ["verdict"] = "watch",
            ["all_runs"] = 1,
            ["first_seen"] = auditDate,
            ["last_seen"] = auditDate,
            ["example_prs"] = new[] { 42 },
            ["ref"] = null
        };
        var processedPath = Path.Combine(memoryPath, "processed-runs.jsonl");
        var watchPath = Path.Combine(memoryPath, "watchlist.jsonl");
        object evidence = new
        {
            auditDate,
            generatedAt = DateTime.UtcNow.ToString("O"),
            records = new[]
            {
                new
                {
                    pr = 42,
                    headSha = new string('a', 40),
                    selection = new
                    {
                        status = "resolved",
                        creditable = true,
                        run = 100,
                        attempt = 1,
                        result = new
                        {
                            selectsAll = true,
                            sourceHasDiff = true,
                            changedFiles = new[] { triggerPath },
                            excludedFiles = Array.Empty<string>(),
                            unattributedFiles = Array.Empty<string>(),
                            testProjects = Array.Empty<string>(),
                            jobs = Array.Empty<string>()
                        }
                    }
                }
            }
        };
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence));
        await File.WriteAllTextAsync(baselinePath, "");
        await File.WriteAllTextAsync(watchBaselinePath, "");
        await File.WriteAllTextAsync(auditDatePath, auditDate + Environment.NewLine);
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        await File.WriteAllTextAsync(watchPath, JsonSerializer.Serialize(watch) + Environment.NewLine);

        var trustedAllEvidence = evidence;
        using var command = new NodeCommand(_testOutput, "test-selection-memory-validation");
        command
            .WithTimeout(TimeSpan.FromSeconds(30))
            .WithEnvironmentVariable("RUNNER_TEMP", workspace.Path);
        var valid = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.True(valid.ExitCode == 0, valid.Output);

        var invalidPaths = new[]
        {
            "/absolute.cs",
            "../outside.cs",
            "src/../outside.cs",
            "src//file.cs",
            "./file.cs",
            " src/file.cs",
            "src/file.cs ",
            "src/\"quoted\".cs",
            "src/back\\slash.cs",
            "src/control\u0001.cs",
            "src/format\u202e.cs",
            "src/line\u2028separator.cs",
        };
        foreach (var invalidPathValue in invalidPaths)
        {
            processed["over_paths"] = new[] { invalidPathValue };
            await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
            var invalidPath = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
            Assert.NotEqual(0, invalidPath.ExitCode);
            Assert.Contains("over_paths[0] is invalid", invalidPath.Output, StringComparison.Ordinal);
        }
        processed["over_paths"] = new[] { triggerPath };
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);

        var baselineProcessed = new Dictionary<string, object?>(processed);
        var baselineWatch = new Dictionary<string, object?>(watch);
        await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(baselineProcessed) + Environment.NewLine);
        await File.WriteAllTextAsync(watchBaselinePath, JsonSerializer.Serialize(baselineWatch) + Environment.NewLine);
        processed["run"] = 101;
        processed["attempt"] = 2;
        processed["all"] = false;
        processed["over_paths"] = Array.Empty<string>();
        evidence = new
        {
            auditDate,
            generatedAt = DateTime.UtcNow.ToString("O"),
            records = new[]
            {
                new
                {
                    pr = 42,
                    headSha = new string('a', 40),
                    selection = new
                    {
                        status = "resolved",
                        creditable = true,
                        run = 101,
                        attempt = 2,
                        result = new
                        {
                            selectsAll = false,
                            sourceHasDiff = true,
                            changedFiles = new[] { triggerPath },
                            excludedFiles = Array.Empty<string>(),
                            unattributedFiles = Array.Empty<string>(),
                            testProjects = Array.Empty<string>(),
                            jobs = Array.Empty<string>()
                        }
                    }
                }
            }
        };
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence));
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        await File.WriteAllTextAsync(watchPath, "");
        var removedZeroCountWatch = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.True(removedZeroCountWatch.ExitCode == 0, removedZeroCountWatch.Output);

        baselineWatch["verdict"] = "correct-by-design";
        await File.WriteAllTextAsync(watchBaselinePath, JsonSerializer.Serialize(baselineWatch) + Environment.NewLine);
        var removedSettledWatch = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.NotEqual(0, removedSettledWatch.ExitCode);
        Assert.Contains("missing baseline watch row", removedSettledWatch.Output, StringComparison.Ordinal);

        var otherProcessed = new Dictionary<string, object?>(baselineProcessed)
        {
            ["pr"] = 43,
            ["sha"] = new string('b', 40),
            ["run"] = 200,
        };
        baselineWatch["verdict"] = "watch";
        baselineWatch["all_runs"] = 2;
        baselineWatch["example_prs"] = new[] { 42, 43 };
        await File.WriteAllTextAsync(
            baselinePath,
            JsonSerializer.Serialize(baselineProcessed) + Environment.NewLine +
            JsonSerializer.Serialize(otherProcessed) + Environment.NewLine);
        await File.WriteAllTextAsync(watchBaselinePath, JsonSerializer.Serialize(baselineWatch) + Environment.NewLine);
        await File.WriteAllTextAsync(
            processedPath,
            JsonSerializer.Serialize(processed) + Environment.NewLine +
            JsonSerializer.Serialize(otherProcessed) + Environment.NewLine);
        var removedContributingWatch = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.NotEqual(0, removedContributingWatch.ExitCode);
        Assert.Contains("missing watchlist row for", removedContributingWatch.Output, StringComparison.Ordinal);

        processed["run"] = 100;
        processed["attempt"] = 1;
        processed["all"] = true;
        processed["over_paths"] = new[] { triggerPath };
        evidence = trustedAllEvidence;
        baselineWatch["all_runs"] = 1;
        baselineWatch["example_prs"] = new[] { 42 };
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence));
        await File.WriteAllTextAsync(baselinePath, "");
        await File.WriteAllTextAsync(watchBaselinePath, "");
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        await File.WriteAllTextAsync(watchPath, JsonSerializer.Serialize(watch) + Environment.NewLine);

        File.Delete(evidencePath);
        var partialProvenance = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.NotEqual(0, partialProvenance.ExitCode);
        Assert.Contains("provenance files are incomplete", partialProvenance.Output, StringComparison.Ordinal);
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence));

        File.Delete(evidencePath);
        File.Delete(baselinePath);
        File.Delete(watchBaselinePath);
        var noProvenance = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.True(noProvenance.ExitCode == 0, noProvenance.Output);
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence));
        await File.WriteAllTextAsync(baselinePath, "");
        await File.WriteAllTextAsync(watchBaselinePath, "");

        processed["seen"] = "2026-02-30";
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        var invalidCalendarDate = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.NotEqual(0, invalidCalendarDate.ExitCode);
        Assert.Contains("must be a UTC date", invalidCalendarDate.Output, StringComparison.Ordinal);

        processed["seen"] = auditDay.AddDays(2).ToString("yyyy-MM-dd");
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        var futureDate = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.NotEqual(0, futureDate.ExitCode);
        Assert.Contains("must be a UTC date", futureDate.Output, StringComparison.Ordinal);

        processed["seen"] = auditDay.AddDays(-1).ToString("yyyy-MM-dd");
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        var backdatedTrustedRow = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.NotEqual(0, backdatedTrustedRow.ExitCode);
        Assert.Contains("seen must match the protected audit date", backdatedTrustedRow.Output, StringComparison.Ordinal);

        processed["seen"] = auditDate;
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        watch["all_runs"] = 2;
        await File.WriteAllTextAsync(watchPath, JsonSerializer.Serialize(watch) + Environment.NewLine);
        var invalid = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.NotEqual(0, invalid.ExitCode);
        Assert.Contains("counter 2 does not match 1 processed heads", invalid.Output, StringComparison.Ordinal);

        watch["all_runs"] = 1;
        await File.WriteAllTextAsync(watchPath, JsonSerializer.Serialize(watch) + Environment.NewLine);
        evidence = new
        {
            auditDate,
            generatedAt = DateTime.UtcNow.ToString("O"),
            records = new[]
            {
                new
                {
                    pr = 42,
                    headSha = new string('a', 40),
                    selection = new
                    {
                        status = "untrusted-fork-artifact",
                        creditable = false,
                        run = 100,
                        attempt = 1,
                        result = new { selectsAll = true }
                    }
                }
            }
        };
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence));
        var untrusted = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.NotEqual(0, untrusted.ExitCode);
        Assert.Contains("is not an unchanged baseline or trusted selection", untrusted.Output, StringComparison.Ordinal);

        await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        watch["verdict"] = "correct-by-design";
        await File.WriteAllTextAsync(watchBaselinePath, JsonSerializer.Serialize(new Dictionary<string, object?>(watch)
        {
            ["verdict"] = "watch"
        }) + Environment.NewLine);
        await File.WriteAllTextAsync(watchPath, JsonSerializer.Serialize(watch) + Environment.NewLine);
        var poisonedWatch = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.NotEqual(0, poisonedWatch.ExitCode);
        Assert.Contains("changes a watch row without trusted current evidence", poisonedWatch.Output, StringComparison.Ordinal);

        var pendingWatch = new Dictionary<string, object?>(watch)
        {
            ["verdict"] = "pending-filed",
            ["note"] = "[test-selection-audit] Fix gitattributes selection",
            ["ref"] = null
        };
        var filedWatch = new Dictionary<string, object?>(pendingWatch)
        {
            ["verdict"] = "filed",
            ["ref"] = 123
        };
        filedWatch.Remove("note");
        await File.WriteAllTextAsync(watchBaselinePath, JsonSerializer.Serialize(pendingWatch) + Environment.NewLine);
        await File.WriteAllTextAsync(watchPath, JsonSerializer.Serialize(filedWatch) + Environment.NewLine);
        var lifecycleTransition = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.True(lifecycleTransition.ExitCode == 0, lifecycleTransition.Output);

        await File.WriteAllTextAsync(processedPath, "");
        var missingBaseline = await command.ExecuteScriptAsync(harnessPath, memoryPath, validationPath);
        Assert.NotEqual(0, missingBaseline.ExitCode);
        Assert.Contains("missing baseline processed row", missingBaseline.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["python"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Uses the Windows Python executable.")]
    public Task TestSelectionAuditCompactsRawMemoryToTheLookbackWindowOnWindows()
        => TestSelectionAuditCompactsRawMemoryToTheLookbackWindow("python");

    [Fact]
    [RequiresTools(["python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses the Unix Python executable.")]
    public Task TestSelectionAuditCompactsRawMemoryToTheLookbackWindowOnUnix()
        => TestSelectionAuditCompactsRawMemoryToTheLookbackWindow("python3");

    private async Task TestSelectionAuditCompactsRawMemoryToTheLookbackWindow(string python)
    {
        using var workspace = TemporaryWorkspace.Create(_testOutput);
        var memoryPath = Path.Combine(workspace.Path, "memory");
        Directory.CreateDirectory(memoryPath);
        var scriptPath = Path.Combine(s_workflowsPath, "test-selection-audit", "compact_memory.py");
        var auditDatePath = Path.Combine(workspace.Path, "gh-aw", "test-selection-audit", "audit-date.txt");

        var recent = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var expired = DateTime.UtcNow.AddDays(-20).ToString("yyyy-MM-dd");
        var processed = new object[]
        {
            new
            {
                pr = 1,
                sha = new string('a', 40),
                run = 1,
                attempt = 1,
                all = true,
                over_paths = new[] { "expired.txt" },
                miss_edges = Array.Empty<object>(),
                seen = expired
            },
            new
            {
                pr = 2,
                sha = new string('b', 40),
                run = 2,
                attempt = 1,
                all = true,
                over_paths = new[] { "retained.txt" },
                miss_edges = Array.Empty<object>(),
                seen = recent
            },
            new
            {
                pr = 2,
                sha = new string('c', 40),
                run = 3,
                attempt = 1,
                all = true,
                over_paths = new[] { "retained.txt" },
                miss_edges = Array.Empty<object>(),
                seen = recent
            }
        };
        var watch = new object[]
        {
            new
            {
                path = "expired.txt",
                rule = "expired.txt",
                rule_ref = "eng/github-ci/test-trigger-map.yml@abcdef1",
                path_ref = "expired.txt@abcdef1",
                kind = "over-selection",
                verdict = "watch",
                all_runs = 1,
                first_seen = expired,
                last_seen = expired,
                example_prs = new[] { 1 },
                @ref = (int?)null
            },
            new
            {
                path = "retained.txt",
                rule = "retained.txt",
                rule_ref = "eng/github-ci/test-trigger-map.yml@abcdef1",
                path_ref = "retained.txt@abcdef1",
                kind = "over-selection",
                verdict = "watch",
                all_runs = 99,
                first_seen = expired,
                last_seen = expired,
                example_prs = new[] { 1 },
                @ref = (int?)null
            },
            new
            {
                path = "settled.txt",
                rule = "settled.txt",
                rule_ref = "eng/github-ci/test-trigger-map.yml@abcdef1",
                path_ref = "settled.txt@abcdef1",
                kind = "over-selection",
                verdict = "correct-by-design",
                all_runs = 4,
                first_seen = expired,
                last_seen = expired,
                example_prs = new[] { 1 },
                @ref = (int?)null
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(memoryPath, "processed-runs.jsonl"),
            string.Join(Environment.NewLine, processed.Select(value => JsonSerializer.Serialize(value))) + Environment.NewLine);
        await File.WriteAllTextAsync(
            Path.Combine(memoryPath, "watchlist.jsonl"),
            string.Join(Environment.NewLine, watch.Select(value => JsonSerializer.Serialize(value))) + Environment.NewLine);

        var result = await ExecutePythonScriptAsync(
            python,
            scriptPath,
            new Dictionary<string, string>
            {
                ["MEMORY_ROOT"] = memoryPath,
                ["RETENTION_DAYS"] = "14",
                ["AUDIT_DATE_PATH"] = auditDatePath,
            });
        Assert.True(result.ExitCode == 0, result.Output);

        var retainedProcessed = File.ReadLines(Path.Combine(memoryPath, "processed-runs.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        Assert.Equal(2, retainedProcessed.Length);
        Assert.All(retainedProcessed, row => Assert.Equal(2, row.GetProperty("pr").GetInt32()));

        var retainedWatch = File.ReadLines(Path.Combine(memoryPath, "watchlist.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        Assert.DoesNotContain(retainedWatch, row => row.GetProperty("path").GetString() == "expired.txt");
        var active = Assert.Single(retainedWatch, row => row.GetProperty("path").GetString() == "retained.txt");
        Assert.Equal(2, active.GetProperty("all_runs").GetInt32());
        Assert.Equal([2], active.GetProperty("example_prs").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(recent, active.GetProperty("first_seen").GetString());
        Assert.Equal(recent, active.GetProperty("last_seen").GetString());

        var settled = Assert.Single(retainedWatch, row => row.GetProperty("path").GetString() == "settled.txt");
        Assert.Equal(0, settled.GetProperty("all_runs").GetInt32());
        Assert.Empty(settled.GetProperty("example_prs").EnumerateArray());
        Assert.Equal("correct-by-design", settled.GetProperty("verdict").GetString());

        var invalidProcessed = new
        {
            pr = 3,
            sha = new string('d', 40),
            run = 4,
            attempt = 1,
            all = true,
            over_paths = Array.Empty<string>(),
            miss_edges = Array.Empty<object>(),
            seen = "2026-02-30"
        };
        await File.WriteAllTextAsync(
            Path.Combine(memoryPath, "processed-runs.jsonl"),
            JsonSerializer.Serialize(invalidProcessed) + Environment.NewLine);
        File.SetAttributes(auditDatePath, FileAttributes.Normal);
        File.Delete(auditDatePath);
        var invalidCalendarDate = await ExecutePythonScriptAsync(
            python,
            scriptPath,
            new Dictionary<string, string>
            {
                ["MEMORY_ROOT"] = memoryPath,
                ["RETENTION_DAYS"] = "14",
                ["AUDIT_DATE_PATH"] = auditDatePath,
            });
        Assert.NotEqual(0, invalidCalendarDate.ExitCode);
        Assert.Contains("must be a real UTC date", invalidCalendarDate.Output, StringComparison.Ordinal);

        var futureProcessed = new
        {
            pr = 4,
            sha = new string('e', 40),
            run = 5,
            attempt = 1,
            all = true,
            over_paths = Array.Empty<string>(),
            miss_edges = Array.Empty<object>(),
            seen = DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd")
        };
        await File.WriteAllTextAsync(
            Path.Combine(memoryPath, "processed-runs.jsonl"),
            JsonSerializer.Serialize(futureProcessed) + Environment.NewLine);
        File.SetAttributes(auditDatePath, FileAttributes.Normal);
        File.Delete(auditDatePath);
        var futureDate = await ExecutePythonScriptAsync(
            python,
            scriptPath,
            new Dictionary<string, string>
            {
                ["MEMORY_ROOT"] = memoryPath,
                ["RETENTION_DAYS"] = "14",
                ["AUDIT_DATE_PATH"] = auditDatePath,
            });
        Assert.NotEqual(0, futureDate.ExitCode);
        Assert.Contains("must be a real UTC date", futureDate.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSelectionAuditDisclosesIssueContractOnlyWhenFiling()
    {
        var workflow = File.ReadAllText(Path.Combine(s_workflowsPath, "test-selection-audit.md"));
        var instructions = ReadTestSelectionAuditFile("issue_instructions.md");

        Assert.Contains(
            ".github/workflows/test-selection-audit/issue_instructions.md",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("The body must contain:", instructions, StringComparison.Ordinal);
        Assert.Contains("**Required validation**", instructions, StringComparison.Ordinal);
        Assert.Contains(
            "<sub>Automated by the weekly CI test-selection audit workflow.</sub>",
            instructions,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TestSelectionAuditKeepsTestFilesInInfrastructureTests()
    {
        var workflowFilesPath = Path.Combine(s_workflowsPath, "test-selection-audit");
        Assert.Empty(Directory.EnumerateFiles(workflowFilesPath, "test_*.py"));

        var testFilesPath = Path.Combine(
            RepoRoot.Path,
            "tests",
            "Infrastructure.Tests",
            "WorkflowScripts",
            "TestSelectionAudit");
        Assert.NotEmpty(Directory.EnumerateFiles(testFilesPath, "test_*.py"));
    }

    [Fact]
    [RequiresTools(["python"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Uses the Windows Python executable.")]
    public Task TestSelectionAuditPythonTestsPassOnWindows() => TestSelectionAuditPythonTestsPass("python");

    [Fact]
    [RequiresTools(["python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses the Unix Python executable.")]
    public Task TestSelectionAuditPythonTestsPassOnUnix() => TestSelectionAuditPythonTestsPass("python3");

    private async Task TestSelectionAuditPythonTestsPass(string python)
    {
        var startInfo = new ProcessStartInfo(python)
        {
            WorkingDirectory = RepoRoot.Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("unittest");
        startInfo.ArgumentList.Add("discover");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("tests/Infrastructure.Tests/WorkflowScripts/TestSelectionAudit");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("test_*.py");
        startInfo.ArgumentList.Add("-v");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {python}.");

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        _testOutput.WriteLine(stdout);
        _testOutput.WriteLine(stderr);

        Assert.True(
            process.ExitCode == 0,
            $"{python} exited with code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }

    private async Task<CommandResult> ExecutePythonScriptAsync(
        string python,
        string scriptPath,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo(python)
        {
            WorkingDirectory = RepoRoot.Path,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {python}.");

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var output = stdout + stderr;
        _testOutput.WriteLine(output);
        return new CommandResult(startInfo, process.ExitCode, output);
    }

    private static YamlMappingNode LoadWorkflow(string fileName)
    {
        var content = File.ReadAllText(Path.Combine(s_workflowsPath, fileName)).ReplaceLineEndings("\n");
        if (fileName.EndsWith(".md", StringComparison.Ordinal))
        {
            content = content[4..content.IndexOf("\n---", 4, StringComparison.Ordinal)];
        }

        var yaml = new YamlStream();
        yaml.Load(new StringReader(content));
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static YamlMappingNode Step(YamlMappingNode root, string name) =>
        Assert.Single(Mappings(root), node => Scalar(node, "name") == name &&
            (node.Children.ContainsKey(new YamlScalarNode("uses")) || node.Children.ContainsKey(new YamlScalarNode("run"))));

    private static YamlMappingNode Mapping(YamlMappingNode node, string key) =>
        Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value.ToString() : "";

    private static string ReadTestSelectionAuditFile(string fileName) =>
        File.ReadAllText(Path.Combine(s_workflowsPath, "test-selection-audit", fileName));

    private static IEnumerable<YamlMappingNode> Mappings(YamlNode node)
    {
        if (node is YamlMappingNode mapping)
        {
            yield return mapping;
        }

        var children = node switch
        {
            YamlMappingNode parent => parent.Children.Values,
            YamlSequenceNode sequence => sequence.Children,
            _ => Enumerable.Empty<YamlNode>()
        };
        foreach (var child in children.SelectMany(Mappings))
        {
            yield return child;
        }
    }
}
