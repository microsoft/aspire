// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Oracle cross-check for <c>docs/ci/test-trigger-map.yml</c> using
/// <see href="https://github.com/leonardochaia/dotnet-affected">dotnet-affected</see> as ground
/// truth: the MSBuild ProjectGraph reverse-dependency closure for a changed project.
///
/// Direction is <b>subset</b>: every test the map's project-level rules select for a source
/// project must be genuinely graph-affected by it (no bogus/over-selecting edges). It is not a
/// superset check — the map's <c>leaf_source</c> is intentionally narrower than the full graph
/// closure (test-hub fan-out is modelled separately), and <c>shared_compiled_source</c> is
/// file-granular, which a project-granular <c>--assume-changes</c> run cannot reproduce. Those
/// dimensions are covered exactly by <see cref="TestTriggerMapTests.LinkCompiledFilesSelectTheirConsumingTests"/>.
///
/// Outerloop + self-skipping: needs the <c>dotnet-affected</c> tool and a restored SDK, neither
/// of which is present in the normal fast suite. Skips cleanly when unavailable.
/// </summary>
public sealed class TestTriggerMapOracleTests
{
    private static readonly TestTriggerMap s_map = TestTriggerMap.Load(RepoRoot.Path);

    [Theory]
    [InlineData("Aspire.Cli")]
    [InlineData("Aspire.Npgsql")]
    [InlineData("Aspire.Hosting.PostgreSQL")]
    [OuterloopTest("Runs dotnet-affected, which needs a restored SDK and an MSBuild ProjectGraph evaluation")]
    public void MapProjectLevelSelectionIsSubsetOfDotnetAffected(string sourceProjectName)
    {
        var tool = ResolveDotnetAffectedOrSkip();
        var probePath = FindSourceProjectRelativePath(sourceProjectName)
            ?? throw new InvalidOperationException($"No src project named '{sourceProjectName}' found.");

        var affected = RunDotnetAffected(tool, sourceProjectName);

        var selected = s_map.SelectTestProjects(probePath, projectLevelOnly: true, out var selectsAll);
        if (selectsAll)
        {
            return; // The probe matched a catch-all rule; subset is trivially satisfied.
        }

        var overSelected = selected.Where(t => !affected.Contains(t)).Order(StringComparer.Ordinal).ToList();
        Assert.True(overSelected.Count == 0,
            $"map selects test projects for '{sourceProjectName}' that dotnet-affected does not report as affected: {string.Join(", ", overSelected)}");
    }

    // dotnet-affected emits one absolute .csproj path per affected/changed project. Keep the
    // test projects (tests/<Name>/<Name>.csproj) and return their <Name>.
    private static IReadOnlySet<string> RunDotnetAffected(string toolPath, string sourceProjectName)
    {
        var outDir = Directory.CreateTempSubdirectory("aspire-affected");
        try
        {
            var psi = new ProcessStartInfo(toolPath)
            {
                WorkingDirectory = RepoRoot.Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // MSBuildLocator (inside dotnet-affected) resolves the SDK via DOTNET_ROOT; point it
            // at the repo-local SDK so the evaluation matches the rest of CI.
            var localSdk = Path.Combine(RepoRoot.Path, ".dotnet");
            if (Directory.Exists(localSdk))
            {
                psi.Environment["DOTNET_ROOT"] = localSdk;
                psi.Environment["PATH"] = localSdk + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            }
            foreach (var arg in new[]
            {
                "--filter-file-path", "Aspire.slnx",
                "--assume-changes", sourceProjectName,
                "--format", "text",
                "--output-dir", outDir.FullName,
                "--output-name", "affected",
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet-affected.");
            var stderr = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"dotnet-affected exited with code {process.ExitCode}.{Environment.NewLine}{stderr}");
            }

            var outputFile = Path.Combine(outDir.FullName, "affected.txt");
            return File.ReadLines(outputFile)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null &&
                    File.Exists(Path.Combine(RepoRoot.Path, "tests", name, $"{name}.csproj")))
                .ToHashSet(StringComparer.Ordinal)!;
        }
        finally
        {
            outDir.Delete(recursive: true);
        }
    }

    private static string ResolveDotnetAffectedOrSkip()
    {
        var candidates = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(home, ".dotnet", "tools", "dotnet-affected"));
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        candidates.AddRange(pathDirs.Where(d => d.Length > 0).Select(d => Path.Combine(d, "dotnet-affected")));

        var tool = candidates.FirstOrDefault(File.Exists);
        if (tool is null)
        {
            Assert.Skip("dotnet-affected is not installed (install with 'dotnet tool install -g dotnet-affected').");
        }
        return tool!;
    }

    private static string? FindSourceProjectRelativePath(string projectName)
    {
        var srcDir = Path.Combine(RepoRoot.Path, "src");
        var match = Directory.EnumerateFiles(srcDir, $"{projectName}.csproj", SearchOption.AllDirectories).FirstOrDefault();
        return match is null ? null : Path.GetRelativePath(RepoRoot.Path, match).Replace('\\', '/');
    }
}
