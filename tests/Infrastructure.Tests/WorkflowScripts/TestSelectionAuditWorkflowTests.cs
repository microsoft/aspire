// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

[Trait("Category", "AgenticWorkflow")]
public sealed class TestSelectionAuditWorkflowTests
{
    private static readonly string s_workflowsPath = Path.Combine(RepoRoot.Path, ".github", "workflows");
    private readonly ITestOutputHelper _testOutput;

    public TestSelectionAuditWorkflowTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task TestSelectionAuditRejectsMemoryCountersNotBackedByProcessedHeads()
    {
        using var workspace = TemporaryWorkspace.Create(_testOutput);
        var memoryPath = Path.Combine(workspace.Path, "memory");
        Directory.CreateDirectory(memoryPath);
        Directory.CreateDirectory(Path.Combine(memoryPath, ".git"));

        var provenancePath = Path.Combine(workspace.Path, "gh-aw", "safeoutputs", "test-selection-audit");
        Directory.CreateDirectory(provenancePath);
        var evidencePath = Path.Combine(provenancePath, "evidence.json");
        var baselinePath = Path.Combine(provenancePath, "processed-runs-before.jsonl");
        var watchBaselinePath = Path.Combine(provenancePath, "watchlist-before.jsonl");
        var auditDatePath = Path.Combine(provenancePath, "audit-date.txt");
        var validationPath = Path.Combine(s_workflowsPath, "test-selection-audit", "validate_memory.js");

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
        var valid = await command.ExecuteScriptAsync(validationPath, memoryPath);
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
            var invalidPath = await command.ExecuteScriptAsync(validationPath, memoryPath);
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
        var removedZeroCountWatch = await command.ExecuteScriptAsync(validationPath, memoryPath);
        Assert.True(removedZeroCountWatch.ExitCode == 0, removedZeroCountWatch.Output);

        baselineWatch["verdict"] = "correct-by-design";
        await File.WriteAllTextAsync(watchBaselinePath, JsonSerializer.Serialize(baselineWatch) + Environment.NewLine);
        var removedSettledWatch = await command.ExecuteScriptAsync(validationPath, memoryPath);
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
        var removedContributingWatch = await command.ExecuteScriptAsync(validationPath, memoryPath);
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
        var partialProvenance = await command.ExecuteScriptAsync(validationPath, memoryPath);
        Assert.NotEqual(0, partialProvenance.ExitCode);
        Assert.Contains("provenance files are incomplete", partialProvenance.Output, StringComparison.Ordinal);
        var partialPushProvenance = await command.ExecuteScriptAsync(
            "-e", "require(process.argv[1]).validateMemory(process.argv[2], false)", validationPath, memoryPath);
        Assert.NotEqual(0, partialPushProvenance.ExitCode);
        Assert.Contains("provenance files are incomplete", partialPushProvenance.Output, StringComparison.Ordinal);
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence));

        File.Delete(evidencePath);
        File.Delete(baselinePath);
        File.Delete(watchBaselinePath);
        var noProvenance = await command.ExecuteScriptAsync(validationPath, memoryPath);
        Assert.NotEqual(0, noProvenance.ExitCode);
        Assert.Contains("provenance files are incomplete", noProvenance.Output, StringComparison.Ordinal);
        var pushValidation = await command.ExecuteScriptAsync(
            "-e", "require(process.argv[1]).validateMemory(process.argv[2], false)", validationPath, memoryPath);
        Assert.True(pushValidation.ExitCode == 0, pushValidation.Output);

        watch["all_runs"] = 2;
        await File.WriteAllTextAsync(watchPath, JsonSerializer.Serialize(watch) + Environment.NewLine);
        var invalidPushCounters = await command.ExecuteScriptAsync(
            "-e", "require(process.argv[1]).validateMemory(process.argv[2], false)", validationPath, memoryPath);
        Assert.NotEqual(0, invalidPushCounters.ExitCode);
        Assert.Contains("counter 2 does not match 1 processed heads", invalidPushCounters.Output, StringComparison.Ordinal);
        watch["all_runs"] = 1;
        await File.WriteAllTextAsync(watchPath, JsonSerializer.Serialize(watch) + Environment.NewLine);
        await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence));
        await File.WriteAllTextAsync(baselinePath, "");
        await File.WriteAllTextAsync(watchBaselinePath, "");

        processed["seen"] = "2026-02-30";
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        var invalidCalendarDate = await command.ExecuteScriptAsync(validationPath, memoryPath);
        Assert.NotEqual(0, invalidCalendarDate.ExitCode);
        Assert.Contains("must be a UTC date", invalidCalendarDate.Output, StringComparison.Ordinal);

        processed["seen"] = auditDay.AddDays(2).ToString("yyyy-MM-dd");
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        var futureDate = await command.ExecuteScriptAsync(validationPath, memoryPath);
        Assert.NotEqual(0, futureDate.ExitCode);
        Assert.Contains("must be a UTC date", futureDate.Output, StringComparison.Ordinal);

        processed["seen"] = auditDay.AddDays(-1).ToString("yyyy-MM-dd");
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        var backdatedTrustedRow = await command.ExecuteScriptAsync(validationPath, memoryPath);
        Assert.NotEqual(0, backdatedTrustedRow.ExitCode);
        Assert.Contains("seen must match the protected audit date", backdatedTrustedRow.Output, StringComparison.Ordinal);

        processed["seen"] = auditDate;
        await File.WriteAllTextAsync(processedPath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        watch["all_runs"] = 2;
        await File.WriteAllTextAsync(watchPath, JsonSerializer.Serialize(watch) + Environment.NewLine);
        var invalid = await command.ExecuteScriptAsync(validationPath, memoryPath);
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
        var untrusted = await command.ExecuteScriptAsync(validationPath, memoryPath);
        Assert.NotEqual(0, untrusted.ExitCode);
        Assert.Contains("is not an unchanged baseline or trusted selection", untrusted.Output, StringComparison.Ordinal);

        await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(processed) + Environment.NewLine);
        watch["verdict"] = "correct-by-design";
        await File.WriteAllTextAsync(watchBaselinePath, JsonSerializer.Serialize(new Dictionary<string, object?>(watch)
        {
            ["verdict"] = "watch"
        }) + Environment.NewLine);
        await File.WriteAllTextAsync(watchPath, JsonSerializer.Serialize(watch) + Environment.NewLine);
        var poisonedWatch = await command.ExecuteScriptAsync(validationPath, memoryPath);
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
        var lifecycleTransition = await command.ExecuteScriptAsync(validationPath, memoryPath);
        Assert.True(lifecycleTransition.ExitCode == 0, lifecycleTransition.Output);

        await File.WriteAllTextAsync(processedPath, "");
        var missingBaseline = await command.ExecuteScriptAsync(validationPath, memoryPath);
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
        var auditDatePath = Path.Combine(workspace.Path, "gh-aw", "safeoutputs", "test-selection-audit", "audit-date.txt");

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
    [RequiresTools(["python"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Uses the Windows Python executable.")]
    public Task TestSelectionAuditPythonTestsPassOnWindows() => TestSelectionAuditPythonTestsPass("python");

    [Fact]
    [RequiresTools(["python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses the Unix Python executable.")]
    public Task TestSelectionAuditPythonTestsPassOnUnix() => TestSelectionAuditPythonTestsPass("python3");

    private async Task TestSelectionAuditPythonTestsPass(string python)
    {
        var result = await TestProcessRunner.RunAsync(
            _testOutput,
            python,
            ["-m", "unittest", "discover", "-s", "tests/Infrastructure.Tests/WorkflowScripts/TestSelectionAudit", "-p", "test_*.py", "-v"],
            RepoRoot.Path);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private Task<ProcessResult> ExecutePythonScriptAsync(
        string python,
        string scriptPath,
        IReadOnlyDictionary<string, string> environment) =>
        TestProcessRunner.RunAsync(_testOutput, python, [scriptPath], RepoRoot.Path, environment);
}
