// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Tests for the SelectTests CLI wiring (<see cref="Selection.Run"/>) — the argument handling and
/// side-channel outputs (<c>$GITHUB_OUTPUT</c> / <c>$GITHUB_STEP_SUMMARY</c>) that surround the
/// engine, as opposed to <see cref="Aspire.SelectTests.TestSelector"/> itself (covered by
/// <see cref="SelectTestsAcceptanceTests"/>). This boundary is the sole gate in enforce mode, so the
/// <c>run_*</c> job booleans, the audit-vs-enforce matrix contract, change resolution, and the
/// degenerate "select nothing" path are the failure modes worth pinning before flipping
/// <c>ENFORCE_SELECTION</c> in <c>tests.yml</c>.
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
    // without coupling to the real eng/test-trigger-map.yml. Token -> run_* name:
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
    // takes on the [full ci] kill switch (force-all, no diff base). Because the selection is ALL, no
    // restriction props are written even under --enforce, so enumerate-tests runs the full matrix.
    [Fact]
    public void ForceAllWithoutDiffInputsWritesNoRestrictionProps()
    {
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            var exitCode = Selection.Run(Options(repoRoot, propsPath, forceAll: true, enforce: true));

            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(propsPath));
            Assert.Equal("", output()["before_build_props"]);
        });
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
            Assert.Equal(propsPath, output()["before_build_props"]);

            var props = File.ReadAllText(propsPath);
            Assert.Contains("<OverrideProjectToBuild Include=\"$(RepoRoot)tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj\" />", props);
            Assert.DoesNotContain("Aspire.Cli.Tests", props);
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
            Assert.Equal("", output()["before_build_props"]);
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

    // P0-4. An ALL selection from a path rule (not just --force-all) must escalate to the full matrix:
    // no restriction props, empty before_build_props, and every run_* true — even under --enforce.
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
            Assert.Equal("", o["before_build_props"]);
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

    // P1-8. A docs-only PR (a changed file that is outside src/**, not ignored, and matched by no
    // rule) selects NOTHING. Under --enforce that writes an OverrideProjectToBuild props with an empty
    // ItemGroup, so the downstream -test build enumerates zero projects — the intended selective-CI
    // outcome (tests.yml then runs no test work). Pin it so a future change can't quietly turn
    // "select nothing" into "select everything" and erase the savings.
    [Fact]
    public void EnforceEmptySelectionWritesEmptyOverride()
    {
        RunInTempRepo((repoRoot, propsPath, output) =>
        {
            var changed = WriteChangedFiles(repoRoot, "docs/readme.md");

            Selection.Run(Options(repoRoot, propsPath, changedFilesPath: changed, skipLayer1: true, enforce: true));

            Assert.True(File.Exists(propsPath));
            Assert.Equal(propsPath, output()["before_build_props"]);
            var props = File.ReadAllText(propsPath);
            Assert.Contains("<ItemGroup>", props);
            Assert.DoesNotContain("OverrideProjectToBuild", props);
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

    private static RunOptions Options(
        string repoRoot,
        string propsPath,
        string? from = null,
        string? to = null,
        string? changedFilesPath = null,
        bool skipLayer1 = false,
        bool forceAll = false,
        bool enforce = false) =>
        new(
            RepoRoot: repoRoot,
            MapPath: Path.Combine(repoRoot, "map.yml"),
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

    private static string RunGit(string repoRoot, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--no-pager");
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
        }

        return stdout.Trim();
    }
}
