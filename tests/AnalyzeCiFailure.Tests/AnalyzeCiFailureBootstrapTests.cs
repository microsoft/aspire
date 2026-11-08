// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Builds and runs the analyzer from a real sparse checkout isolated from hostile parent imports.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Fact]
    [RequiresTools(["git", "dotnet"])]
    public async Task AnalyzerBuildsFromRealSparseCheckoutWithoutHostileParentImports()
    {
        var seed = Directory.CreateDirectory(Path.Combine(_workspace.Path, "seed")).FullName;
        var analyzerDirectory = Path.Combine(RepoRoot.Path, "tools", "AnalyzeCiFailure");
        foreach (var relative in new[] { "global.json", "NuGet.config", "eng/test-retry-patterns.json" }
            .Concat(Directory.GetFiles(Path.Combine(RepoRoot.Path, "tools", "Aspire.TestTools"), "*.cs")
                .Select(file => Path.GetRelativePath(RepoRoot.Path, file)))
            .Concat(Directory.GetFiles(analyzerDirectory, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetRelativePath(analyzerDirectory, file).Split(Path.DirectorySeparatorChar)[0] is not ("bin" or "obj"))
                .Select(file => Path.GetRelativePath(RepoRoot.Path, file))))
        {
            var target = Path.Combine(seed, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(RepoRoot.Path, relative), target);
        }
        foreach (var args in new string[][]
        {
            ["--no-pager", "-C", seed, "init"],
            ["--no-pager", "-C", seed, "config", "user.name", "Test"],
            ["--no-pager", "-C", seed, "config", "user.email", "test@example.invalid"],
            ["--no-pager", "-C", seed, "add", "--", "global.json", "NuGet.config", "tools/AnalyzeCiFailure", "tools/Aspire.TestTools", "eng/test-retry-patterns.json"],
            ["--no-pager", "-C", seed, "commit", "-m", "Seed sparse bootstrap fixture"],
        })
        {
            var setup = await RunProcessAsync("git", args);
            Assert.True(setup.ExitCode == 0, setup.Output);
        }
        var hostile = Directory.CreateDirectory(Path.Combine(_workspace.Path, "hostile")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(hostile, "Directory.Build.props"),
            """<Project><Import Project="missing-hostile-parent.props" /></Project>""");
        await File.WriteAllTextAsync(
            Path.Combine(hostile, "Directory.Build.targets"),
            """<Project><Target Name="Poison" BeforeTargets="Build"><Error Text="Hostile parent targets were imported" /></Target></Project>""");
        await File.WriteAllTextAsync(
            Path.Combine(hostile, "Directory.Packages.props"),
            """<Project><Import Project="missing-hostile-packages.props" /></Project>""");
        var checkout = Path.Combine(hostile, "checkout");
        foreach (var args in new string[][]
        {
            ["--no-pager", "clone", "--no-checkout", seed, checkout],
            ["--no-pager", "-C", checkout, "sparse-checkout", "init", "--no-cone"],
            ["--no-pager", "-C", checkout, "sparse-checkout", "set", "--no-cone",
                "/global.json", "/NuGet.config", "/tools/AnalyzeCiFailure/", "/tools/Aspire.TestTools/", "/eng/test-retry-patterns.json"],
            ["--no-pager", "-C", checkout, "checkout"],
        })
        {
            var setup = await RunProcessAsync("git", args);
            Assert.True(setup.ExitCode == 0, setup.Output);
        }
        var project = Path.Combine(checkout, "tools", "AnalyzeCiFailure", "AnalyzeCiFailure.csproj");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var restore = await RunProcessAsync(dotnet, ["restore", project, "--verbosity", "quiet"], workingDirectory: checkout);
        Assert.True(restore.ExitCode == 0, restore.Output);
        var build = await RunProcessAsync(dotnet, ["build", project, "--no-restore", "--configuration", "Release", "--verbosity", "quiet"], workingDirectory: checkout);
        Assert.True(build.ExitCode == 0, build.Output);
        var run = await RunProcessAsync(
            dotnet,
            [Path.Combine(checkout, "tools", "AnalyzeCiFailure", "bin", "Release", "net11.0", "AnalyzeCiFailure.dll")],
            environment: new Dictionary<string, string> { ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1" },
            workingDirectory: checkout);
        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Required command was not provided.", run.Output, StringComparison.Ordinal);
        Assert.Contains("Usage:", run.Output, StringComparison.Ordinal);

        var results = await WritePreparationFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(results, "00001.trx"), "<TestRun><Results/></TestRun>");
        var parsed = await RunPreparationAsync();
        Assert.True(parsed.ExitCode == 0, parsed.Output);
        var evidenceDirectory = EvidenceDirectory;
        foreach (var source in Directory.GetFiles(evidenceDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(checkout, "ci-failure-analysis/evidence", Path.GetRelativePath(evidenceDirectory, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        await File.WriteAllTextAsync(
            Path.Combine(checkout, "ci-failure-analysis/evidence", "analysis-summary.md"),
            "Copied input summary must not satisfy the sparse replay assertion.");
        var preparation = await RunProcessAsync(
            dotnet,
            [Path.Combine(checkout, "tools", "AnalyzeCiFailure", "bin", "Release", "net11.0", "AnalyzeCiFailure.dll"),
                "replay", "--evidence-dir", Path.Combine(checkout, "ci-failure-analysis/evidence"), "--working-dir", checkout],
            workingDirectory: checkout);
        Assert.True(preparation.ExitCode == 0, preparation.Output);
        var replayDirectory = ReadNamedPath(preparation.Output, "replay_dir");
        Assert.Contains(
            "## Prior Causes (from memory branch)",
            await File.ReadAllTextAsync(Path.Combine(replayDirectory, "analysis-summary.md")),
            StringComparison.Ordinal);
        Assert.Equal(
            "Copied input summary must not satisfy the sparse replay assertion.",
            await File.ReadAllTextAsync(Path.Combine(checkout, "ci-failure-analysis/evidence", "analysis-summary.md")));
    }
}
