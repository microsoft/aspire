// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Tests for the SelectTests CLI wiring (<see cref="Selection.Run"/>) — the argument handling and
/// side-channel outputs that surround the engine, as opposed to
/// <see cref="Aspire.SelectTests.TestSelector"/> itself (covered by <see cref="SelectTestsAcceptanceTests"/>).
/// The selector runs BEFORE enumerate-tests, so it derives its test-project universe from
/// <c>Aspire.slnx</c> and (only when enforcing a non-ALL selection) writes an OverrideProjectToBuild
/// props file that restricts the downstream <c>-test</c> build.
/// </summary>
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

    private const string TriggerMap = """
        version: 1
        path_rules:
          - paths: [trigger.txt]
            targets: ["test:Aspire.Hosting.Tests"]
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
            var exitCode = Selection.Run(new RunOptions(
                RepoRoot: repoRoot,
                MapPath: Path.Combine(repoRoot, "map.yml"),
                From: null,
                To: null,
                ChangedFilesPath: null,
                SkipLayer1: false,
                ForceAll: true,
                Enforce: true,
                BeforeBuildProps: propsPath));

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
            var changed = Path.Combine(repoRoot, "changed.txt");
            File.WriteAllText(changed, "trigger.txt\n");

            var exitCode = Selection.Run(new RunOptions(
                RepoRoot: repoRoot,
                MapPath: Path.Combine(repoRoot, "map.yml"),
                From: null,
                To: null,
                ChangedFilesPath: changed,
                SkipLayer1: true,
                ForceAll: false,
                Enforce: true,
                BeforeBuildProps: propsPath));

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
            var changed = Path.Combine(repoRoot, "changed.txt");
            File.WriteAllText(changed, "trigger.txt\n");

            var exitCode = Selection.Run(new RunOptions(
                RepoRoot: repoRoot,
                MapPath: Path.Combine(repoRoot, "map.yml"),
                From: null,
                To: null,
                ChangedFilesPath: changed,
                SkipLayer1: true,
                ForceAll: false,
                Enforce: false,
                BeforeBuildProps: propsPath));

            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(propsPath));
            Assert.Equal("", output()["before_build_props"]);
        });
    }

    // Sets up a hermetic repo (Aspire.slnx + map.yml) and redirects the GitHub Actions side-channel
    // files into the temp dir, then runs the body. The third argument re-reads $GITHUB_OUTPUT into a
    // key/value map on demand.
    private static void RunInTempRepo(Action<string, string, Func<IReadOnlyDictionary<string, string>>> body)
    {
        var dir = Directory.CreateTempSubdirectory("selecttests-cli");
        var prevOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        var prevSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "Aspire.slnx"), Slnx);
            File.WriteAllText(Path.Combine(dir.FullName, "map.yml"), TriggerMap);

            var outputPath = Path.Combine(dir.FullName, "output");
            Environment.SetEnvironmentVariable("GITHUB_OUTPUT", outputPath);
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", Path.Combine(dir.FullName, "summary"));

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

            body(dir.FullName, Path.Combine(dir.FullName, "BeforeBuildProps.props"), ReadOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_OUTPUT", prevOutput);
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", prevSummary);
            Directory.Delete(dir.FullName, recursive: true);
        }
    }
}
