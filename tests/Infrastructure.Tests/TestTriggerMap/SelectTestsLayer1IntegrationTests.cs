// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.SelectTests;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// End-to-end integration of <see cref="Selection.Run"/> with Layer 1 (the in-process
/// <see cref="GraphAffectedProjects"/> graph) enabled — the wiring the CLI tests deliberately skip
/// via <c>--skip-layer1</c>. Builds a real MSBuild graph (a production project + a test project that
/// references it) and asserts that a change to the production source flows through the graph closure,
/// is intersected with the slnx test-project universe, and lands in the enforce-mode
/// OverrideProjectToBuild props.
/// </summary>
[Collection("GraphAffectedProjects")] // MSBuildLocator registers process-wide; keep these serialized.
public sealed class SelectTestsLayer1IntegrationTests
{
    public SelectTestsLayer1IntegrationTests()
    {
        GraphAffectedProjects.EnsureMSBuildRegistered();
    }

    // Failure mode: the selector wires Layer 1's affected set into the result incorrectly (drops it,
    // fails to intersect with the matrix, or never reaches the props), so a production-only change
    // silently runs no tests in enforce mode. A change to src/Core/Core.cs must reach Core.Tests
    // (which references Core) via the reverse-dependency closure and be written to the props.
    [Fact]
    public void ProductionChangeFlowsThroughLayer1IntoEnforceProps()
    {
        using var fixture = new GraphRepoFixture();

        var changed = fixture.WriteChangedFiles("src/Core/Core.cs");
        var propsPath = System.IO.Path.Combine(fixture.Path, "BeforeBuildProps.props");

        fixture.WithGitHubEnvRedirected(output =>
        {
            var exit = Selection.Run(new RunOptions(
                RepoRoot: fixture.Path,
                MapPath: System.IO.Path.Combine(fixture.Path, "map.yml"),
                From: null,
                To: null,
                ChangedFilesPath: changed,
                SkipLayer1: false,
                ForceAll: false,
                Enforce: true,
                BeforeBuildProps: propsPath));

            Assert.Equal(0, exit);
            Assert.Equal(propsPath, output()["before_build_props"]);
            Assert.Contains("tests/Core.Tests/Core.Tests.csproj", File.ReadAllText(propsPath));
        });
    }

    /// <summary>
    /// A temp repo with a real, buildable MSBuild graph: <c>src/Core</c> (production) and
    /// <c>tests/Core.Tests</c> (a test project referencing it), plus an <c>Aspire.slnx</c> and an
    /// empty <c>map.yml</c> (Layer 1 alone does the work here).
    /// </summary>
    private sealed class GraphRepoFixture : IDisposable
    {
        private readonly TestTempDirectory _temp = new();

        public string Path => _temp.Path;

        public GraphRepoFixture()
        {
            Write("Directory.Build.props", "<Project />");
            Write("Directory.Build.targets", "<Project />");
            Write("map.yml", "version: 1\n");

            Write("src/Core/Core.cs", "namespace Core; public class C { }");
            WriteProject("src/Core/Core.csproj", compiles: ["Core.cs"], references: []);

            Write("tests/Core.Tests/Core.Tests.cs", "namespace Core.Tests; public class T { }");
            WriteProject("tests/Core.Tests/Core.Tests.csproj", compiles: ["Core.Tests.cs"], references: [@"..\..\src\Core\Core.csproj"]);

            Write("Aspire.slnx",
                """
                <Solution>
                  <Project Path="src/Core/Core.csproj" />
                  <Project Path="tests/Core.Tests/Core.Tests.csproj" />
                </Solution>
                """);
        }

        public string WriteChangedFiles(params string[] paths)
        {
            var changed = System.IO.Path.Combine(_temp.Path, "changed.txt");
            File.WriteAllLines(changed, paths);
            return changed;
        }

        public void WithGitHubEnvRedirected(Action<Func<IReadOnlyDictionary<string, string>>> body)
        {
            var prevOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
            var prevSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            try
            {
                var outputPath = System.IO.Path.Combine(_temp.Path, "output");
                Environment.SetEnvironmentVariable("GITHUB_OUTPUT", outputPath);
                Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", System.IO.Path.Combine(_temp.Path, "summary"));

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

        private void Write(string relativePath, string contents)
        {
            var fullPath = System.IO.Path.Combine(_temp.Path, relativePath.Replace('\\', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, contents);
        }

        private void WriteProject(string relativePath, string[] compiles, string[] references)
        {
            var items = string.Join(Environment.NewLine,
                compiles.Select(c => $"""    <Compile Include="{c}" Link="{System.IO.Path.GetFileName(c)}" />""")
                    .Concat(references.Select(r => $"""    <ProjectReference Include="{r}" />""")));

            Write(relativePath,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                {items}
                  </ItemGroup>
                </Project>
                """);
        }

        public void Dispose() => _temp.Dispose();
    }
}
