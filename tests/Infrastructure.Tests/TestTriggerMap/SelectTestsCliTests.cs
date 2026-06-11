// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Tests for the SelectTests CLI wiring (<see cref="Selection.Run"/>) — the argument handling that
/// surrounds the engine, as opposed to <see cref="Aspire.SelectTests.TestSelector"/> itself (covered
/// by <see cref="SelectTestsAcceptanceTests"/>).
/// </summary>
public sealed class SelectTestsCliTests
{
    private const string MinimalMap = """
        version: 1
        path_rules:
          - paths: [global.json]
            targets: [ALL]
        """;

    private const string MatrixJson =
        """{"include":[{"projectName":"Aspire.Hosting.Tests","runs-on":"ubuntu-latest"},{"projectName":"Aspire.Cli.Tests","runs-on":"ubuntu-latest"}]}""";

    // Regression: --force-all means "run the full matrix regardless of the diff", so it must NOT
    // require a --from/--changed-files input. Run() previously resolved changed files *before* the
    // force-all short-circuit and threw "Provide either --changed-files or --from"; the CI step then
    // swallowed that non-zero exit, silently masking a broken selector. This drives the exact path
    // the workflow takes on the [full ci] kill switch (force-all, no diff base).
    [Fact]
    public void ForceAllWithoutDiffInputsEmitsFullMatrix()
    {
        var dir = Directory.CreateTempSubdirectory("selecttests-cli");
        try
        {
            var mapPath = Path.Combine(dir.FullName, "map.yml");
            var matrixPath = Path.Combine(dir.FullName, "all_tests.json");
            var outputPath = Path.Combine(dir.FullName, "selected_matrix.json");
            File.WriteAllText(mapPath, MinimalMap);
            File.WriteAllText(matrixPath, MatrixJson);

            // Redirect the GitHub Actions side-channel files into the temp dir so the run is hermetic
            // when these are set by a real CI runner; restored in finally.
            var prevOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
            var prevSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            Environment.SetEnvironmentVariable("GITHUB_OUTPUT", Path.Combine(dir.FullName, "output"));
            Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", Path.Combine(dir.FullName, "summary"));

            int exitCode;
            try
            {
                exitCode = Selection.Run(new RunOptions(
                    RepoRoot: dir.FullName,
                    MapPath: mapPath,
                    MatrixPath: matrixPath,
                    From: null,
                    To: null,
                    ChangedFilesPath: null,
                    DotnetAffected: null,
                    SkipLayer1: false,
                    ForceAll: true,
                    Enforce: false,
                    Output: outputPath));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GITHUB_OUTPUT", prevOutput);
                Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", prevSummary);
            }

            Assert.Equal(0, exitCode);

            // Audit + force-all passes the full matrix through unfiltered.
            using var expected = JsonDocument.Parse(MatrixJson);
            using var actual = JsonDocument.Parse(File.ReadAllText(outputPath));
            Assert.Equal(
                expected.RootElement.GetProperty("include").GetArrayLength(),
                actual.RootElement.GetProperty("include").GetArrayLength());
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }
}
