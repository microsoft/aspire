// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Tests for the SelectTests CLI wiring (<see cref="Selection.Run"/>) — the argument handling and
/// side-channel outputs (<c>$GITHUB_OUTPUT</c> / <c>$GITHUB_STEP_SUMMARY</c>) that surround the
/// engine, as opposed to <see cref="Aspire.SelectTests.TestSelector"/> itself (covered by
/// <see cref="SelectTestsAcceptanceTests"/>). This boundary is the sole gate in enforce mode, so the
/// <c>run_*</c> job booleans, the audit-vs-enforce matrix contract, change resolution, and the
/// degenerate "select nothing" path are the failure modes worth pinning before flipping the
/// <c>select-tests</c> action's <c>enforce</c> input in <c>tests.yml</c>.
/// </summary>
// Shares the collection with the other classes that mutate the process-wide GITHUB_OUTPUT /
// GITHUB_STEP_SUMMARY env vars (and the MSBuildLocator registration), so they never run concurrently
// and clobber each other's side-channel files.
[Collection("GraphAffectedProjects")]
public sealed class SelectTestsCliTests
{
    // A hermetic Aspire.slnx: two test projects (the universe) plus a fixture project that must be
    // excluded (no .Tests suffix). Only the text is parsed; the .csproj files need not exist.
    private const string Slnx = """
        <Solution>
          <Project Path="tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj" />
          <Project Path="tests/Aspire.Cli.Tests/Aspire.Cli.Tests.csproj" />
          <Project Path="tests/Aspire.TestUtilities/Aspire.TestUtilities.csproj" />
        </Solution>
        """;

    // A synthetic map that carries job: targets in three shapes — referenced directly by a path
    // rule, only via a group, and only via a derived rule — so the run_* contract can be exercised
    // without coupling to the real eng/github-ci/test-trigger-map.yml. Token -> run_* name:
    //   job:extension-e2e    -> run_extension_e2e   (direct; also pins the '-' -> '_' mapping)
    //   job:group-job        -> run_group_job       (reachable ONLY through GROUP_ONLY_JOB)
    //   job:derived-only-job -> run_derived_only_job (reachable ONLY through a derived_targets rule)
    private const string Map = """
        version: 1
        groups:
          GROUP_ONLY_JOB: [job:group-job]
        path_rules:
          - paths: [trigger.txt]
            targets: ["test:Aspire.Hosting.Tests", "job:extension-e2e"]
          - paths: [other.txt]
            targets: ["test:Aspire.Cli.Tests"]
          - paths: [all.txt]
            targets: [ALL]
          - paths: [grp.txt]
            targets: [GROUP_ONLY_JOB]
          - paths: [prod.txt]
            targets: ["test:Aspire.Hosting", "test:Aspire.Hosting.Tests"]
        derived_targets:
          - tests: [test:Aspire.Cli.Tests]
            targets: [job:derived-only-job]
        """;

    // Regression: --force-all means "run everything regardless of the diff", so it must NOT require a
    // --from/--changed-files input. Run() previously resolved changed files *before* the force-all
    // short-circuit and threw "Provide either --changed-files or --from"; the CI step then swallowed
    // that non-zero exit, silently masking a broken selector. This drives the exact path the workflow
    // takes on the run-full-ci label kill switch (force-all, no diff base). Because the selection is ALL, no
    // restriction props are written even under --enforce, so enumerate-tests runs the full matrix.
    [Fact]
    public void ForceAllWithoutDiffInputsWritesNoRestrictionProps()
    {
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            var exitCode = Selection.Run(Options(repoRoot, propsPath, forceAll: true, enforce: true));

            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(propsPath));
            Assert.Equal("", output()["project_override_props"]);
        });
    }

    // The --slnx option points the selector at a solution outside the default <repo-root>/Aspire.slnx.
    // Failure mode: if SlnxPath were ignored and the tool fell back to <repo-root>/Aspire.slnx, the
    // universe would be read from the wrong (here: absent) file and LoadTestProjects would throw, so a
    // change that maps to a test project would never reach the enforce props. Pins that a custom
    // solution path is honored end-to-end (so the select-tests action's `slnx` input is real).
    [Fact]
    public void CustomSlnxPathIsHonored()
    {
        var dir = Directory.CreateTempSubdirectory("selecttests-slnx");
        try
        {
            // Deliberately NOT at <repo-root>/Aspire.slnx -- the default lookup would mask the option.
            var slnxPath = Path.Combine(dir.FullName, "custom", "MyApp.slnx");
            Directory.CreateDirectory(Path.GetDirectoryName(slnxPath)!);
            File.WriteAllText(slnxPath, Slnx);
            File.WriteAllText(Path.Combine(dir.FullName, "map.yml"), Map);

            WithGitHubEnv(dir.FullName, _ =>
            {
                var propsPath = Path.Combine(dir.FullName, "BeforeBuildProps.props");
                var changed = WriteChangedFiles(dir.FullName, "trigger.txt");

                var exit = Selection.Run(Options(
                    dir.FullName, propsPath, changedFilesPath: changed,
                    skipLayer1: true, enforce: true, slnxPath: slnxPath));

                Assert.Equal(0, exit);
                Assert.Contains("Aspire.Hosting.Tests", File.ReadAllText(propsPath));
            });
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    // Enforce + a non-ALL selection writes the OverrideProjectToBuild props for exactly the selected
    // test projects (mapped to their Aspire.slnx paths), and reports the props path so the workflow
    // can pass /p:BeforeBuildPropsPath to enumerate-tests.
    [Fact]
    public void EnforceWritesOverridePropsForSelectedSubset()
    {
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            var changed = WriteChangedFiles(repoRoot, "trigger.txt");

            var exitCode = Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            Assert.Equal(0, exitCode);
            Assert.Equal(propsPath, output()["project_override_props"]);
            // A non-empty selection enumerates its subset, so the .NET matrix is built.
            Assert.Equal("true", output()["has_dotnet_tests"]);

            var props = File.ReadAllText(propsPath);
            Assert.Contains("<OverrideProjectToBuild Include=\"$(RepoRoot)tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj\" />", props);
            Assert.DoesNotContain("Aspire.Cli.Tests", props);
        });
    }

    // The PR comment (SELECT_TESTS_COMMENT_FILE) is the terse, scannable view: a "## Tests
    // selector" heading, the selected test projects, and the selected jobs -- and none of the
    // step-summary audit detail (options, changed files, would-have-skipped). Pin that so the comment
    // stays reader-friendly, and that enforcing mode omits the "(audit mode)" qualifier.
    [Fact]
    public void WritesConciseSelectionComment()
    {
        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var commentPath = Path.Combine(repoRoot, "comment.md");
            var previous = Environment.GetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE");
            Environment.SetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE", commentPath);
            try
            {
                var changed = WriteChangedFiles(repoRoot, "trigger.txt");

                Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

                var comment = File.ReadAllText(commentPath);
                Assert.StartsWith("## Tests selector", comment);
                Assert.DoesNotContain("audit mode", comment);
                Assert.Contains("### Selected test projects (1 / 2)", comment);
                Assert.Contains("`Aspire.Hosting.Tests`", comment);
                Assert.Contains("### Selected jobs (1)", comment);
                Assert.Contains("`extension-e2e`", comment);
                // Test projects are the primary signal, so their section must come BEFORE the jobs
                // section. Falsifies a revert to the old jobs-first ordering.
                Assert.True(
                    comment.IndexOf("### Selected test projects", StringComparison.Ordinal)
                        < comment.IndexOf("### Selected jobs", StringComparison.Ordinal),
                    "Selected test projects must be listed before Selected jobs.");
                // The rationale is collapsed by default behind a <details> so the comment leads with
                // what runs; the heading is the <summary>. Falsifies a revert to a plain "### How these
                // were chosen" heading that is always expanded.
                Assert.Contains("<summary>How these were chosen — grouped by what changed</summary>", comment);
                Assert.DoesNotContain("### How these were chosen", comment);
                // The job-reasons table attributes each selected job to what triggered it
                // (extension-e2e <- trigger.txt). Falsifies a revert that drops the table.
                Assert.Contains("| Job | Triggered by |", comment);
                Assert.Contains("| `extension-e2e` | `trigger.txt` |", comment);
                Assert.DoesNotContain("### Options", comment);
                Assert.DoesNotContain("Changed files", comment);
                Assert.DoesNotContain("Would have been", comment);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE", previous);
            }
        });
    }

    // The PR comment attributes EVERY cause that selected an item — no truncation, no "(+N more)"
    // tail. A reviewer must see exactly which changed files / edges pulled each test in. Here both
    // trigger.txt and prod.txt route to Aspire.Hosting.Tests, so the grouped "how chosen" section must
    // show both files as triggers (the project appears under each). Failure mode: dropping a trigger
    // hides why a test was selected.
    [Fact]
    public void CommentListsEveryCauseWithoutTruncation()
    {
        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var commentPath = Path.Combine(repoRoot, "comment.md");
            var previous = Environment.GetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE");
            Environment.SetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE", commentPath);
            try
            {
                var changed = WriteChangedFiles(repoRoot, "trigger.txt", "prod.txt");

                Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

                var comment = File.ReadAllText(commentPath);
                // Each changed file is its own group heading, and the "directly" bucket under it lists
                // exactly the projects that file pulled in. Asserting the grouped (by-trigger) shape --
                // file heading + "→ **N** directly: ..." -- falsifies a revert to the flat per-project
                // rendering, which had no such per-file buckets. prod.txt selects both projects;
                // trigger.txt selects only the test project.
                Assert.Contains("`prod.txt`** *(changed)*", comment);
                Assert.Contains("→ **2** directly: `Aspire.Hosting`, `Aspire.Hosting.Tests`", comment);
                Assert.Contains("`trigger.txt`** *(changed)*", comment);
                Assert.Contains("→ **1** directly: `Aspire.Hosting.Tests`", comment);
                Assert.DoesNotContain("more)", comment);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE", previous);
            }
        });
    }

    // A job pulled in by SEVERAL independent triggers must render each as its OWN bulleted line, not
    // comma-joined: a comma between e.g. "affected project X" and a selected-test reason reads as a
    // single causal chain when they are unrelated. Here job:multi is hit by a path rule (a.txt) AND a
    // derived_targets pull from the selected test Aspire.Cli.Tests; the cell must bullet the two and
    // name the trigger as "selected test" (a noun, parallel to "affected project"). Failure mode:
    // regressing to comma-joining makes "affected project X, selected test Y" look like one chain.
    [Fact]
    public void JobWithIndependentCausesRendersEachReasonOnItsOwnLine()
    {
        const string slnx = """
            <Solution>
              <Project Path="tests/Aspire.Cli.Tests/Aspire.Cli.Tests.csproj" />
            </Solution>
            """;
        const string map = """
            version: 1
            path_rules:
              - paths: [a.txt]
                targets: ["job:multi"]
              - paths: [b.txt]
                targets: ["test:Aspire.Cli.Tests"]
            derived_targets:
              - tests: [test:Aspire.Cli.Tests]
                targets: [job:multi]
            """;

        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var commentPath = Path.Combine(repoRoot, "comment.md");
            var previous = Environment.GetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE");
            Environment.SetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE", commentPath);
            try
            {
                var changed = WriteChangedFiles(repoRoot, "a.txt", "b.txt");

                Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

                var comment = File.ReadAllText(commentPath);
                // The two independent triggers are bulleted on separate lines (<br>), and the derived
                // pull reads "selected test" -- pinning the bullets, the separation, and the wording in
                // one exact cell match.
                Assert.Contains("| `multi` | • `a.txt`<br>• selected test `Aspire.Cli.Tests` |", comment);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE", previous);
            }
        }, slnx: slnx, map: map);
    }

    // The headline call-out (⚠️ "N of the M ... come from a single change") and the <details> collapse
    // in RenderProjectList are both threshold-gated (headline: tests >= 10 && largest group >= 5;
    // collapse: inline limit of 12). A single change that fans out to many projects must trip both.
    // Failure mode: a refactor silently drops the headline or stops collapsing large buckets, making
    // big selections unreadable again — the very problem this comment layout exists to solve.
    [Fact]
    public void LargeFanOutEmitsHeadlineAndCollapsesProjectList()
    {
        var projects = Enumerable.Range(1, 14).Select(i => $"Aspire.Pkg{i:00}.Tests").ToList();
        var slnx = "<Solution>\n"
            + string.Join("\n", projects.Select(p => $"  <Project Path=\"tests/{p}/{p}.csproj\" />"))
            + "\n</Solution>";
        // One path rule maps a single changed file to all 14 test projects, so they land in one group.
        var map = "version: 1\npath_rules:\n  - paths: [big.txt]\n    targets: ["
            + string.Join(", ", projects.Select(p => $"\"test:{p}\""))
            + "]\n";

        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var commentPath = Path.Combine(repoRoot, "comment.md");
            var previous = Environment.GetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE");
            Environment.SetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE", commentPath);
            try
            {
                var changed = WriteChangedFiles(repoRoot, "big.txt");

                Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

                var comment = File.ReadAllText(commentPath);
                // Headline names the count and the single change that drove it.
                Assert.Contains("⚠️ 14 of the 14 selected test projects come from a single change — `big.txt`", comment);
                // The 14-project bucket (over the inline limit of 12) collapses into a <details>.
                Assert.Contains("<details><summary>show 14</summary>", comment);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE", previous);
            }
        }, slnx: slnx, map: map);
    }

    // In audit mode the comment is advisory: the full matrix and all jobs still run, and the lists
    // describe what selective CI WOULD run under enforcement. Pin the "(audit mode)" title qualifier
    // and the explanatory line so the advisory framing can't silently disappear — without it a reader
    // could mistake the selected subset for what actually ran.
    [Fact]
    public void AuditCommentMarksSelectionAsAdvisory()
    {
        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var commentPath = Path.Combine(repoRoot, "comment.md");
            var previous = Environment.GetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE");
            Environment.SetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE", commentPath);
            try
            {
                var changed = WriteChangedFiles(repoRoot, "trigger.txt");

                Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: false));

                var comment = File.ReadAllText(commentPath);
                Assert.StartsWith("## Tests selector (audit mode)", comment);
                Assert.Contains("**would**", comment);
                Assert.Contains("under enforcement", comment);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SELECT_TESTS_COMMENT_FILE", previous);
            }
        });
    }

    // The JSON selection artifact (SELECT_TESTS_JSON_FILE) is the durable, machine-readable record of a
    // selection: mode, inputs (to reproduce), and EVERY selected test/job with its per-item causes. It's
    // what a maintainer downloads weeks later to see why something ran, without re-running CI. Pin the
    // schema essentials and that per-item causes (with the triggering file) survive serialization.
    [Fact]
    public void WritesSelectionJsonArtifactWithPerItemCauses()
    {
        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var jsonPath = Path.Combine(repoRoot, "selection.json");
            var previous = Environment.GetEnvironmentVariable("SELECT_TESTS_JSON_FILE");
            Environment.SetEnvironmentVariable("SELECT_TESTS_JSON_FILE", jsonPath);
            try
            {
                // trigger.txt -> test:Aspire.Hosting.Tests + job:extension-e2e (both via the same path rule).
                var changed = WriteChangedFiles(repoRoot, "trigger.txt");

                Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = doc.RootElement;
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("enforcing", root.GetProperty("mode").GetString());
                Assert.False(root.GetProperty("selectsAll").GetBoolean());
                Assert.Equal($"changed-files {changed}", root.GetProperty("inputs").GetProperty("changeSource").GetString());

                var test = root.GetProperty("testProjects").EnumerateArray()
                    .Single(t => t.GetProperty("name").GetString() == "Aspire.Hosting.Tests");
                var testCause = test.GetProperty("causes").EnumerateArray().Single();
                Assert.Equal("PathRule", testCause.GetProperty("kind").GetString());
                Assert.Equal("trigger.txt", testCause.GetProperty("trigger").GetString());

                var job = root.GetProperty("jobs").EnumerateArray()
                    .Single(j => j.GetProperty("name").GetString() == "job:extension-e2e");
                Assert.Equal("PathRule", job.GetProperty("causes").EnumerateArray().Single().GetProperty("kind").GetString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("SELECT_TESTS_JSON_FILE", previous);
            }
        });
    }

    // Crash traceability hardening: the diagnostics writer must be best-effort. If the step summary
    // path is unwritable (the exact scenario where diagnostics matter), writing the block must NOT throw
    // a NEW exception that masks the ORIGINAL failure. The original (FileNotFoundException from the
    // missing --changed-files) must still propagate. Failure mode (before the fix): File.AppendAllText
    // throws DirectoryNotFoundException from the catch handler, replacing the real root cause.
    [Fact]
    public void FailureDiagnosticsWriteFailureDoesNotMaskOriginalException()
    {
        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var missing = Path.Combine(repoRoot, "does-not-exist.txt");
            // A summary path under a directory that does not exist -> File.AppendAllText throws.
            var unwritableSummary = Path.Combine(repoRoot, "no", "such", "dir", "summary.md");
            var previousSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", unwritableSummary);
            try
            {
                // The ORIGINAL failure (missing changed-files) must surface, not a DirectoryNotFoundException
                // about the summary path. FileNotFoundException : IOException; DirectoryNotFoundException
                // also : IOException, so assert the concrete original type.
                Assert.Throws<FileNotFoundException>(() =>
                    Selection.Run(Options(repoRoot, propsPath, changedFilesPath: missing, skipLayer1: true, enforce: true)));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", previousSummary);
            }
        });
    }

    // Traceability: when the Layer 1 graph computation crashes, the wrapper must PRESERVE the original
    // exception as InnerException so the diagnostics' stack trace points at where MSBuild actually failed,
    // not at Layer1Failed. The hermetic slnx references .csproj files that don't exist on disk, so
    // building the ProjectGraph (skipLayer1:false) fails deterministically. Failure mode (before the fix):
    // Layer1Failed(ex.Message) discards the original exception, so InnerException is null and the real
    // crash location is lost.
    [Fact]
    public void Layer1FailurePreservesOriginalExceptionAsInner()
    {
        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var changed = WriteChangedFiles(repoRoot, "src/whatever/File.cs");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: false, enforce: true)));

            Assert.Contains("Layer 1", ex.Message, StringComparison.Ordinal);
            Assert.NotNull(ex.InnerException);
        });
    }

    // Audit mode (no --enforce) writes the run_* booleans and the summary but no restriction props,
    // so enumerate-tests enumerates the full matrix unchanged even when a subset was selected.
    [Fact]
    public void AuditWritesNoRestrictionProps()
    {
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            var changed = WriteChangedFiles(repoRoot, "trigger.txt");

            var exitCode = Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: false));

            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(propsPath));
            Assert.Equal("", output()["project_override_props"]);
        });
    }

    // P0-1. Audit mode forces every run_* boolean to true even when the computed selection is a
    // strict subset, because enumerate-tests still runs the FULL matrix in audit — gating a non-.NET
    // job off while running every .NET test would be an inconsistent, partial audit run.
    [Fact]
    public void AuditForcesEveryJobBooleanTrue()
    {
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            // trigger.txt selects only Aspire.Hosting.Tests + job:extension-e2e; group-job and
            // derived-only-job are NOT selected. Audit must still report them all true.
            var changed = WriteChangedFiles(repoRoot, "trigger.txt");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: false));

            var o = output();
            Assert.Equal("true", o["run_extension_e2e"]);
            Assert.Equal("true", o["run_group_job"]);
            Assert.Equal("true", o["run_derived_only_job"]);
        });
    }

    // P0-2. Enforce emits the real per-job value for each job, and maps the job: token to its run_*
    // name (strip "job:", '-' -> '_'). A mistranslated name never matches its if: in tests.yml (the
    // job silently never runs); an unselected job must be 'false', not unset.
    [Fact]
    public void EnforceEmitsPerJobBooleansWithNameMapping()
    {
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            // trigger.txt selects job:extension-e2e only.
            var changed = WriteChangedFiles(repoRoot, "trigger.txt");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            var o = output();
            Assert.Equal("true", o["run_extension_e2e"]);
            Assert.Equal("false", o["run_group_job"]);
            Assert.Equal("false", o["run_derived_only_job"]);
        });
    }

    // P0-3. Every job the map can ever emit appears as a run_* key, regardless of selection — even a
    // job reachable ONLY through a group (group-job) or ONLY through a derived rule (derived-only-job).
    // A job omitted from AllJobTokens() would have its if: read an empty string and silently never run.
    // (output() flattens the `selection` JSON back to run_* keys; see ReadOutput.)
    [Fact]
    public void EveryMapJobAppearsAsRunBoolean()
    {
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            var changed = WriteChangedFiles(repoRoot, "trigger.txt");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            var keys = output().Keys;
            Assert.Contains("run_extension_e2e", keys);
            Assert.Contains("run_group_job", keys);
            Assert.Contains("run_derived_only_job", keys);
        });
    }

    // P0-3b. The job gates ship as ONE `selection` output holding a JSON object of real booleans, not
    // one output per job. tests.yml consumes it via fromJSON(...).run_<job>, so a regression to flat
    // run_* outputs (or to string values) would break every non-.NET job's if:. Asserts the raw output
    // shape directly rather than through the flattening helper.
    [Fact]
    public void JobGatesEmittedAsSingleSelectionJsonObject()
    {
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            var changed = WriteChangedFiles(repoRoot, "trigger.txt");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            // Assert on the raw `selection` output directly (not the run_* keys the helper also
            // flattens out of it): the on-the-wire contract is a single `selection` key whose value
            // parses as a JSON object of booleans.
            var raw = output();
            Assert.Contains("selection", raw.Keys);

            using var doc = JsonDocument.Parse(raw["selection"]);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            // trigger.txt selects job:extension-e2e only -> true; group/derived jobs -> false.
            Assert.True(doc.RootElement.GetProperty("run_extension_e2e").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("run_group_job").GetBoolean());
        });
    }

    // P0-4. An ALL selection from a path rule (not just --force-all) must escalate to the full matrix:
    // no restriction props, empty project_override_props, and every run_* true — even under --enforce.
    // The failure mode is an ALL escalation being filtered down to whatever was otherwise selected,
    // under-running on a run-everything trigger.
    [Fact]
    public void EnforceWithAllPathRuleRunsEverything()
    {
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            var changed = WriteChangedFiles(repoRoot, "all.txt");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            var o = output();
            Assert.False(File.Exists(propsPath));
            Assert.Equal("", o["project_override_props"]);
            Assert.Equal("true", o["run_extension_e2e"]);
            Assert.Equal("true", o["run_group_job"]);
            Assert.Equal("true", o["run_derived_only_job"]);
        });
    }

    // P1-5. With neither --from nor --changed-files and not --force-all, there is no way to know what
    // changed, so Run must throw rather than silently selecting nothing. This is the non-force-all
    // sibling of the regression ForceAllWithoutDiffInputs... guards: the guard must not be reordered
    // after Layer 1 so that this input combination quietly under-selects.
    [Fact]
    public void NoDiffInputsAndNoForceAllThrows()
    {
        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                Selection.Run(Options(repoRoot, propsPath, skipLayer1: true, enforce: true)));

            Assert.Contains("--changed-files", ex.Message, StringComparison.Ordinal);
        });
    }

    // Crash traceability: when the selector throws mid-run, the CI step must still fail loudly (so a
    // crash never silently under-selects), AND the failure must be debuggable -- a diagnostics block in
    // the step summary naming the stage it died in and the inputs needed to reproduce. A non-existent
    // --changed-files path fails deterministically in the "resolve changed files" stage. Failure mode:
    // a bare stack trace with no record of WHAT it was processing or HOW to re-run it.
    [Fact]
    public void FailureEmitsDiagnosticsNamingStageAndInputsThenRethrows()
    {
        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var missing = Path.Combine(repoRoot, "does-not-exist.txt");

            // The original failure must still surface (FileNotFoundException : IOException), not be
            // swallowed -- the diagnostics augment it, they don't replace the non-zero exit.
            Assert.ThrowsAny<IOException>(() =>
                Selection.Run(Options(repoRoot, propsPath, changedFilesPath: missing, skipLayer1: true, enforce: true)));

            var summary = File.ReadAllText(Path.Combine(repoRoot, "summary"));
            Assert.Contains("SelectTests FAILED", summary);
            Assert.Contains("resolve changed files", summary);
            // The exact input that reproduces the crash.
            Assert.Contains(missing, summary);
        });
    }

    // P1-6. --from/--to is an endpoint-to-endpoint (two-dot) diff, NOT a three-dot merge-base diff.
    // The repo below diverges so the two differ: feature adds trigger.txt off a base commit, then the
    // base advances by editing other.txt. A two-dot diff(advanced-base, feature) reports BOTH files;
    // a three-dot diff would report only trigger.txt. Selecting Aspire.Cli.Tests (other.txt's target)
    // therefore proves two-dot semantics — an accidental switch to '...' would drop it and silently
    // change which files (and tests) a moved-base PR selects.
    [Fact]
    public void FromToUsesTwoDotDiffSemantics()
    {
        WithGitRepo((repoRoot, output) =>
        {
            WriteFile(repoRoot, "Aspire.slnx", Slnx);
            WriteFile(repoRoot, "map.yml", Map);
            WriteFile(repoRoot, "other.txt", "v0");
            GitCommitAll(repoRoot, "base");
            var baseSha = RunGit(repoRoot, "rev-parse", "HEAD");

            RunGit(repoRoot, "checkout", "-q", "-b", "feature");
            WriteFile(repoRoot, "trigger.txt", "x");
            GitCommitAll(repoRoot, "feature change");
            var featureSha = RunGit(repoRoot, "rev-parse", "HEAD");

            // Advance the base after the branch point so two-dot and three-dot diverge.
            RunGit(repoRoot, "checkout", "-q", "-b", "advanced-base", baseSha);
            WriteFile(repoRoot, "other.txt", "v1");
            GitCommitAll(repoRoot, "base advances");
            var advancedBaseSha = RunGit(repoRoot, "rev-parse", "HEAD");

            var propsPath = Path.Combine(repoRoot, "BeforeBuildProps.props");
            Selection.Run(Options(repoRoot, propsPath, from: advancedBaseSha, to: featureSha, skipLayer1: true, enforce: true));

            var props = File.ReadAllText(propsPath);
            // trigger.txt (added on feature) -> Aspire.Hosting.Tests; other.txt (differs across the two
            // endpoints) -> Aspire.Cli.Tests. The latter is present only under two-dot.
            Assert.Contains("Aspire.Hosting.Tests", props);
            Assert.Contains("Aspire.Cli.Tests", props);
        });
    }

    // P1-6b. --from with no --to diffs the base ref against the WORKING TREE, so an uncommitted edit is
    // picked up. Failure mode: requiring --to (or diffing against HEAD instead of the work tree) would
    // miss locally-changed files when the workflow runs the selector against the checked-out tree.
    [Fact]
    public void FromWithoutToDiffsAgainstWorkingTree()
    {
        WithGitRepo((repoRoot, output) =>
        {
            WriteFile(repoRoot, "Aspire.slnx", Slnx);
            WriteFile(repoRoot, "map.yml", Map);
            WriteFile(repoRoot, "other.txt", "v0");
            GitCommitAll(repoRoot, "base");
            var baseSha = RunGit(repoRoot, "rev-parse", "HEAD");

            // Uncommitted working-tree edit.
            WriteFile(repoRoot, "other.txt", "v1");

            var propsPath = Path.Combine(repoRoot, "BeforeBuildProps.props");
            Selection.Run(Options(repoRoot, propsPath, from: baseSha, to: null, skipLayer1: true, enforce: true));

            var props = File.ReadAllText(propsPath);
            Assert.Contains("Aspire.Cli.Tests", props);
            Assert.DoesNotContain("Aspire.Hosting.Tests", props);
        });
    }

    // P1-6c. A rename must attribute BOTH sides so a file moved OUT of a mapped directory still runs
    // that directory's tests. git's default rename detection reports only the destination, hiding the
    // old path; the selector passes --no-renames so the diff decomposes into delete(old)+add(new).
    // other.txt (-> Aspire.Cli.Tests) is renamed to renamed.txt, which the map ignores so the new side
    // adds nothing and does not trip the run-all fallback -- isolating the assertion to the old side:
    // the deletion of other.txt must still select Aspire.Cli.Tests. Failure mode: dropping --no-renames
    // makes git hide other.txt, so only the ignored renamed.txt is seen, the rule never fires, and the
    // move silently skips its tests.
    [Fact]
    public void RenameOutOfMappedPathStillSelectsItsTests()
    {
        const string map = """
            version: 1
            path_rules:
              - paths: [other.txt]
                targets: ["test:Aspire.Cli.Tests"]
            ignore:
              - renamed.txt
            """;

        WithGitRepo((repoRoot, output) =>
        {
            WriteFile(repoRoot, "Aspire.slnx", Slnx);
            WriteFile(repoRoot, "map.yml", map);
            WriteFile(repoRoot, "other.txt", "v0");
            GitCommitAll(repoRoot, "base");
            var baseSha = RunGit(repoRoot, "rev-parse", "HEAD");

            RunGit(repoRoot, "mv", "other.txt", "renamed.txt");
            GitCommitAll(repoRoot, "rename other.txt out of its mapped path");
            var headSha = RunGit(repoRoot, "rev-parse", "HEAD");

            var propsPath = Path.Combine(repoRoot, "BeforeBuildProps.props");
            Selection.Run(Options(repoRoot, propsPath, from: baseSha, to: headSha, skipLayer1: true, enforce: true));

            Assert.Contains("Aspire.Cli.Tests", File.ReadAllText(propsPath));
        });
    }

    // Regression: a changed file whose repo-relative path contains non-ASCII bytes must still be
    // attributed. git's default core.quotePath=true octal-escapes and double-quotes such paths
    // (e.g. "eng/\343\203\206.../trigger.cs"), which does not glob-equal the real path, so the rule
    // never fires. The selector passes -c core.quotePath=false so git emits the literal UTF-8 path and
    // the rule matches, putting Aspire.Hosting.Tests in the enforce props (asserted below). If the flag
    // regressed, the mangled path would match no rule and fall to the run-all fallback, which writes NO
    // restriction props -- so the assertion below would fail (props missing/empty) and catch it. A CJK
    // dir name is used deliberately (no NFC/NFD decomposition) so the test is stable on case/normalizing
    // filesystems while still exercising the non-ASCII path.
    [Fact]
    public void NonAsciiChangedPathUnderAMappedDirStillSelectsItsTests()
    {
        const string unicodeMap = """
            version: 1
            path_rules:
              - paths: ["eng/テスト/**"]
                targets: ["test:Aspire.Hosting.Tests"]
            """;

        WithGitRepo((repoRoot, output) =>
        {
            WriteFile(repoRoot, "Aspire.slnx", Slnx);
            WriteFile(repoRoot, "map.yml", unicodeMap);
            WriteFile(repoRoot, "placeholder.txt", "v0");
            GitCommitAll(repoRoot, "base");
            var baseSha = RunGit(repoRoot, "rev-parse", "HEAD");

            WriteFile(repoRoot, "eng/テスト/trigger.cs", "// changed");
            GitCommitAll(repoRoot, "add a non-ASCII path under a mapped directory");
            var headSha = RunGit(repoRoot, "rev-parse", "HEAD");

            var propsPath = Path.Combine(repoRoot, "BeforeBuildProps.props");
            Selection.Run(Options(repoRoot, propsPath, from: baseSha, to: headSha, skipLayer1: true, enforce: true));

            Assert.Contains("Aspire.Hosting.Tests", File.ReadAllText(propsPath));
        });
    }

    // P1-7. --changed-files trims surrounding whitespace and drops blank lines before glob matching.
    // A regression that fed padded/blank paths to the globber would match nothing — " trigger.txt "
    // does not glob-equal "trigger.txt" — so the surrounding-whitespace line below must still select
    // Aspire.Hosting.Tests.
    [Fact]
    public void ChangedFilesTrimsWhitespaceAndBlankLines()
    {
        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var changed = Path.Combine(repoRoot, "changed.txt");
            File.WriteAllText(changed, "\n  trigger.txt  \n\n\t\n");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            Assert.Contains("Aspire.Hosting.Tests", File.ReadAllText(propsPath));
        });
    }

    // P1-8. A selection that resolves only to non-.NET jobs (here grp.txt -> GROUP_ONLY_JOB ->
    // job:group-job: a job target, no test: target, not ALL) selects no buildable .NET test project.
    // Under --enforce that is the "no .NET tests" case: SelectTests writes no restriction props and
    // signals has_dotnet_tests=false, so tests.yml skips enumerate-tests and emits an empty matrix.
    // (An empty OverrideProjectToBuild would instead make the build fall back to the whole solution and
    // fail.) Pin it so a future change can't turn "select nothing buildable" into "build everything",
    // and so the non-.NET-job-only path keeps skipping the .NET matrix. (A genuinely unmapped file is a
    // different case -- the run-all fallback -- and is covered separately.)
    [Fact]
    public void EnforceJobOnlySelectionSignalsNoDotnetTests()
    {
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            var changed = WriteChangedFiles(repoRoot, "grp.txt");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            Assert.Equal("false", output()["has_dotnet_tests"]);
            Assert.Equal("", output()["project_override_props"]);
            Assert.False(File.Exists(propsPath));
        });
    }

    // P1-9. A selected name that is not a buildable test project in Aspire.slnx (e.g. a production
    // project name pulled in by a rule) contributes NO OverrideProjectToBuild item — only real
    // tests/<Name>/<Name>.csproj projects do. Failure mode: a non-test project name leaking into the
    // -test build list. prod.txt selects both "Aspire.Hosting" (production, not in the slnx test set)
    // and "Aspire.Hosting.Tests"; only the latter must become an item.
    [Fact]
    public void EnforceSkipsNonTestProjectNamesInOverride()
    {
        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            var changed = WriteChangedFiles(repoRoot, "prod.txt");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            var props = File.ReadAllText(propsPath);
            var itemCount = props.Split("OverrideProjectToBuild Include=").Length - 1;
            Assert.Equal(1, itemCount);
            Assert.Contains("tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj", props);
        });
    }

    // P1-10. The --skip-layer1 footgun: with Layer 1 disabled there is no graph attribution, so a
    // src/** file under a real solution project dir must still fall to the run-all fallback rather
    // than be treated as "Layer-1-owned" and silently select nothing. Failure mode (before the fix):
    // project dirs were loaded even under --skip-layer1, so the file looked owned, no rule matched,
    // and --enforce reported has_dotnet_tests=false -- a real source change skipping all .NET tests.
    [Fact]
    public void EnforceSkipLayer1SrcFileUnderProjectStillForcesRunAll()
    {
        const string slnx = """
            <Solution>
              <Project Path="tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj" />
              <Project Path="src/Aspire.Managed/Aspire.Managed.csproj" />
            </Solution>
            """;

        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            // Matched by no map rule and under a solution project dir.
            var changed = WriteChangedFiles(repoRoot, "src/Aspire.Managed/Program.cs");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            // Run-all: the full matrix is enumerated and no restriction props are written.
            Assert.Equal("true", output()["has_dotnet_tests"]);
            Assert.Equal("", output()["project_override_props"]);
            Assert.False(File.Exists(propsPath));
        }, slnx: slnx);
    }

    // Pre-filter: changed files matching a pattern in the (runtime-read) skip-gate patterns file are
    // dropped BEFORE both layers, so a docs-only change selects nothing even when a path rule would
    // otherwise route it to ALL. keep_routed carve-outs (files the selector routes) are never dropped.
    [Fact]
    public void PrefilterDropsPatternFileMatchesButHonorsKeepRouted()
    {
        const string mapWithPrefilter = """
            version: 1
            prefilter:
              patterns_file: skip-patterns.txt
              keep_routed:
                - .github/workflows/**
            path_rules:
              - paths: [docs/**]
                targets: [ALL]
              - paths: [.github/workflows/**]
                targets: ["test:Aspire.Hosting.Tests"]
            """;

        // A docs-only .md change is dropped (patterns file has **.md) -> empty selection (NOT ALL), even
        // though docs/** -> ALL would have matched it.
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            File.WriteAllText(Path.Combine(repoRoot, "skip-patterns.txt"), "# docs\n**.md\n");
            var changed = WriteChangedFiles(repoRoot, "docs/guide.md");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            Assert.Equal("false", output()["has_dotnet_tests"]);
            var summary = File.ReadAllText(Path.Combine(repoRoot, "summary"));
            Assert.Contains("Pre-filtered (excluded) files (1)", summary);
            Assert.Contains("docs/guide.md", summary);
        }, map: mapWithPrefilter);

        // keep_routed carve-out: a workflow file the patterns file lists is NOT dropped, so its rule fires.
        RunInTempRepo((repoRoot, propsPath, _) =>
        {
            File.WriteAllText(Path.Combine(repoRoot, "skip-patterns.txt"), ".github/workflows/**\n");
            var changed = WriteChangedFiles(repoRoot, ".github/workflows/ci.yml");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            Assert.Contains("Aspire.Hosting.Tests", File.ReadAllText(propsPath));
        }, map: mapWithPrefilter);
    }

    private static RunOptions Options(
        string repoRoot,
        string propsPath,
        string? from = null,
        string? to = null,
        string? changedFilesPath = null,
        bool skipLayer1 = false,
        bool forceAll = false,
        bool enforce = false,
        string? slnxPath = null) =>
        new(
            RepoRoot: repoRoot,
            MapPath: Path.Combine(repoRoot, "map.yml"),
            SlnxPath: slnxPath ?? Path.Combine(repoRoot, "Aspire.slnx"),
            From: from,
            To: to,
            ChangedFilesPath: changedFilesPath,
            SkipLayer1: skipLayer1,
            ForceAll: forceAll,
            Enforce: enforce,
            BeforeBuildProps: propsPath);

    private static string WriteChangedFiles(string repoRoot, params string[] paths)
    {
        var changed = Path.Combine(repoRoot, "changed.txt");
        File.WriteAllLines(changed, paths);
        return changed;
    }

    // Sets up a hermetic repo (Aspire.slnx + map.yml) and redirects the GitHub Actions side-channel
    // files into the temp dir, then runs the body. The third argument re-reads $GITHUB_OUTPUT into a
    // key/value map on demand.
    private static void RunInTempRepo(
        Action<string, string, Func<IReadOnlyDictionary<string, string>>> body,
        string slnx = Slnx,
        string map = Map)
    {
        var dir = Directory.CreateTempSubdirectory("selecttests-cli");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "Aspire.slnx"), slnx);
            File.WriteAllText(Path.Combine(dir.FullName, "map.yml"), map);

            WithGitHubEnv(dir.FullName, output =>
                body(dir.FullName, Path.Combine(dir.FullName, "BeforeBuildProps.props"), output));
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    // A temp git repo (no slnx/map written for you — the body sets up exactly the history it needs),
    // with the GitHub Actions side channels redirected.
    private static void WithGitRepo(Action<string, Func<IReadOnlyDictionary<string, string>>> body)
    {
        var dir = Directory.CreateTempSubdirectory("selecttests-git");
        try
        {
            RunGit(dir.FullName, "init", "-q", "-b", "main");
            RunGit(dir.FullName, "config", "user.email", "test@example.com");
            RunGit(dir.FullName, "config", "user.name", "Test");
            RunGit(dir.FullName, "config", "commit.gpgsign", "false");

            WithGitHubEnv(dir.FullName, output => body(dir.FullName, output));
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    private static void WithGitHubEnv(string dir, Action<Func<IReadOnlyDictionary<string, string>>> body)
    {
        var prevOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        var prevSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        try
        {
            var outputPath = Path.Combine(dir, "output");
            Environment.SetEnvironmentVariable("GITHUB_OUTPUT", outputPath);
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", Path.Combine(dir, "summary"));

            IReadOnlyDictionary<string, string> ReadOutput()
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                if (File.Exists(outputPath))
                {
                    foreach (var line in File.ReadAllLines(outputPath))
                    {
                        var eq = line.IndexOf('=', StringComparison.Ordinal);
                        if (eq >= 0)
                        {
                            map[line[..eq]] = line[(eq + 1)..];
                        }
                    }
                }

                // The run_<job> gates are emitted as ONE JSON object under `selection` (tests.yml reads
                // it via fromJSON). Expand it back to flat run_* string entries so the per-job assertions
                // keep the shape the tool used to emit one-key-per-job; deserializing as bool also pins
                // that every gate is present and a real JSON boolean.
                if (map.TryGetValue("selection", out var selectionJson))
                {
                    foreach (var (name, value) in JsonSerializer.Deserialize<Dictionary<string, bool>>(selectionJson)!)
                    {
                        map[name] = value ? "true" : "false";
                    }
                }

                return map;
            }

            body(ReadOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_OUTPUT", prevOutput);
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", prevSummary);
        }
    }

    private static void WriteFile(string repoRoot, string relativePath, string contents)
    {
        var fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
    }

    private static void GitCommitAll(string repoRoot, string message)
    {
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", message);
    }

    private static string RunGit(string repoRoot, params string[] args) => GitCli.Run(repoRoot, args);
}
