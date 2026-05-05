// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for eng/scripts/filter-test-matrix-by-scope.ps1
/// </summary>
public class FilterTestMatrixByScopeTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public FilterTestMatrixByScopeTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "filter-test-matrix-by-scope.ps1");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RunAll_PassesThrough()
    {
        var matrix = CreateMatrix(
            CreateEntry("Proj-linux", "tests/ProjA/ProjA.csproj"),
            CreateEntry("Proj-win", "tests/ProjB/ProjB.csproj"));

        var result = await RunFilter(matrix, "[]", runAll: true);

        result.EnsureSuccessful("RunAll should pass through");
        var filtered = ParseOutputMatrix(result.Output, "test_matrix");
        Assert.Equal(2, filtered.Length);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RunAll_PassesThroughSingleIncludeObject()
    {
        var matrix = CreateMatrixWithSingleIncludeObject(
            CreateEntry("Only-linux", "tests/Only/Only.csproj"));

        var result = await RunFilter(matrix, "[]", runAll: true);

        result.EnsureSuccessful("RunAll should pass through a single include object");
        var filtered = ParseOutputMatrix(result.Output, "test_matrix");
        var onlyEntry = Assert.Single(filtered);
        Assert.Equal("Only-linux", onlyEntry.GetProperty("shortname").GetString());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RunAll_PassesThroughEmptyIncludeArray()
    {
        var matrix = CreateMatrix();

        var result = await RunFilter(matrix, "[]", runAll: true);

        result.EnsureSuccessful("RunAll should pass through an empty include array");
        var filtered = ParseOutputMatrix(result.Output, "test_matrix");
        Assert.Empty(filtered);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RunAll_FailsWithHelpfulErrorForInvalidIncludeType()
    {
        const string matrix = """
            {"include":"oops"}
            """;

        var result = await RunFilter(matrix, "[]", runAll: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Matrix 'test_matrix' has an invalid 'include' value of type", result.Output);
        Assert.Contains("System.String", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task EmptyAffectedList_PassesThrough()
    {
        var matrix = CreateMatrix(
            CreateEntry("Proj-linux", "tests/ProjA/ProjA.csproj"));

        var result = await RunFilter(matrix, "[]", runAll: false);

        result.EnsureSuccessful("Empty affected list should pass through");
        var filtered = ParseOutputMatrix(result.Output, "test_matrix");
        Assert.Single(filtered);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task InvalidAffectedProjectsJson_FailsWithHelpfulError()
    {
        var matrix = CreateMatrix(
            CreateEntry("Proj-linux", "tests/ProjA/ProjA.csproj"));

        var result = await RunFilter(matrix, "[tests/ProjA/ProjA.csproj]", runAll: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("AffectedProjects must be a JSON array string.", result.Output);
        Assert.Contains("[tests/ProjA/ProjA.csproj]", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task InvalidDefaultCoverageProjectsJson_FailsWithHelpfulError()
    {
        var matrix = CreateMatrix(
            CreateEntry("Proj-linux", "tests/ProjA/ProjA.csproj"));

        var result = await RunFilter(matrix, "[]", runAll: false, defaultCoverageProjects: "[tests/ProjA/ProjA.csproj]");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("DefaultCoverageProjects must be a JSON array string.", result.Output);
        Assert.Contains("[tests/ProjA/ProjA.csproj]", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FiltersToAffectedProjects()
    {
        var matrix = CreateMatrix(
            CreateEntry("ProjA-linux", "tests/ProjA/ProjA.csproj"),
            CreateEntry("ProjB-linux", "tests/ProjB/ProjB.csproj"),
            CreateEntry("ProjC-linux", "tests/ProjC/ProjC.csproj"));

        var affected = JsonSerializer.Serialize(new[] { "tests/ProjA/ProjA.csproj", "tests/ProjC/ProjC.csproj" });

        var result = await RunFilter(matrix, affected, runAll: false);

        result.EnsureSuccessful("Should filter to affected projects");
        var filtered = ParseOutputMatrix(result.Output, "test_matrix");
        Assert.Equal(2, filtered.Length);
        Assert.Contains(filtered, e => e.GetProperty("shortname").GetString() == "ProjA-linux");
        Assert.Contains(filtered, e => e.GetProperty("shortname").GetString() == "ProjC-linux");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task NoMatchingProjects_ReturnsEmptyMatrix()
    {
        var matrix = CreateMatrix(
            CreateEntry("ProjA-linux", "tests/ProjA/ProjA.csproj"));

        var affected = JsonSerializer.Serialize(new[] { "tests/Unrelated/Unrelated.csproj" });

        var result = await RunFilter(matrix, affected, runAll: false);

        result.EnsureSuccessful("Should return empty matrix");
        var filtered = ParseOutputMatrix(result.Output, "test_matrix");
        Assert.Empty(filtered);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task DefaultCoverageProjects_KeepPreferredRunnerWhenNotDirectlyAffected()
    {
        var matrix = CreateMatrix(
            CreateEntry("ProjA-linux", "tests/ProjA/ProjA.csproj", runsOn: "ubuntu-latest"),
            CreateEntry("Templates-linux", "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj", runsOn: "ubuntu-latest"),
            CreateEntry("Templates-win", "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj", runsOn: "windows-latest"));

        var affected = JsonSerializer.Serialize(new[] { "tests/ProjA/ProjA.csproj" });
        var defaultCoverage = JsonSerializer.Serialize(new[] { "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj" });

        var result = await RunFilter(matrix, affected, runAll: false, defaultCoverageProjects: defaultCoverage);

        result.EnsureSuccessful("Default coverage project should be kept on the preferred runner");
        var filtered = ParseOutputMatrix(result.Output, "test_matrix");
        Assert.Equal(2, filtered.Length);
        Assert.Contains(filtered, e => e.GetProperty("shortname").GetString() == "ProjA-linux");
        Assert.Contains(filtered, e => e.GetProperty("shortname").GetString() == "Templates-linux");
        Assert.DoesNotContain(filtered, e => e.GetProperty("shortname").GetString() == "Templates-win");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task DirectlyAffectedProjects_KeepAllRunnersEvenWhenInDefaultCoverage()
    {
        var matrix = CreateMatrix(
            CreateEntry("Templates-linux", "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj", runsOn: "ubuntu-latest"),
            CreateEntry("Templates-win", "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj", runsOn: "windows-latest"));

        var affected = JsonSerializer.Serialize(new[] { "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj" });
        var defaultCoverage = JsonSerializer.Serialize(new[] { "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj" });

        var result = await RunFilter(matrix, affected, runAll: false, defaultCoverageProjects: defaultCoverage);

        result.EnsureSuccessful("Directly affected projects should keep all runners");
        var filtered = ParseOutputMatrix(result.Output, "test_matrix");
        Assert.Equal(2, filtered.Length);
        Assert.Contains(filtered, e => e.GetProperty("shortname").GetString() == "Templates-linux");
        Assert.Contains(filtered, e => e.GetProperty("shortname").GetString() == "Templates-win");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task AuditOnly_LogsButDoesNotFilter()
    {
        var matrix = CreateMatrix(
            CreateEntry("ProjA-linux", "tests/ProjA/ProjA.csproj"),
            CreateEntry("ProjB-linux", "tests/ProjB/ProjB.csproj"));

        var affected = JsonSerializer.Serialize(new[] { "tests/ProjA/ProjA.csproj" });

        var result = await RunFilter(matrix, affected, runAll: false, auditOnly: true);

        result.EnsureSuccessful("AuditOnly should succeed");
        var filtered = ParseOutputMatrix(result.Output, "test_matrix");
        // AuditOnly returns full matrix unchanged
        Assert.Equal(2, filtered.Length);
        // But logs should mention what would be filtered
        Assert.Contains("AUDIT", result.Output);
        Assert.Contains("[WOULD RUN] tests/ProjA/ProjA.csproj", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task AuditOnly_WithGitHubOutput_WritesWouldRunProjects()
    {
        var matrix = CreateMatrix(
            CreateEntry("ProjA-linux", "tests/ProjA/ProjA.csproj"),
            CreateEntry("ProjB-linux", "tests/ProjB/ProjB.csproj"));

        var affected = JsonSerializer.Serialize(new[] { "tests/ProjA/ProjA.csproj" });

        var (result, githubOutputPath) = await RunFilterWithGitHubOutput(matrix, affected, runAll: false, auditOnly: true);

        result.EnsureSuccessful("AuditOnly GitHub output should succeed");

        var projectsJson = GetGitHubOutputValue(githubOutputPath, "audit_would_run_projects");
        Assert.NotNull(projectsJson);

        var projects = JsonSerializer.Deserialize<string[]>(projectsJson!);
        Assert.NotNull(projects);
        Assert.Equal(["tests/ProjA/ProjA.csproj"], projects);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task AuditOnly_WithAuditFile_WritesStructuredAuditJson()
    {
        var matrix = CreateMatrix(
            CreateEntry("Templates-linux", "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"),
            CreateEntry("ProjB-linux", "tests/ProjB/ProjB.csproj"));

        var affected = JsonSerializer.Serialize(new[] { "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj" });

        var (result, githubOutputPath, auditPath) = await RunFilterWithGitHubOutputAndAuditFile(matrix, affected, runAll: false, auditOnly: true);

        result.EnsureSuccessful("AuditOnly with audit file should succeed");
        Assert.NotNull(GetGitHubOutputValue(githubOutputPath, "audit_would_run_projects"));
        Assert.True(File.Exists(auditPath));

        using var auditDocument = JsonDocument.Parse(await File.ReadAllTextAsync(auditPath));
        var root = auditDocument.RootElement;

        Assert.False(root.GetProperty("runAll").GetBoolean());
        Assert.True(root.GetProperty("auditOnly").GetBoolean());
        Assert.True(root.GetProperty("templateGate").GetProperty("wouldRun").GetBoolean());

        var wouldRunProjects = root.GetProperty("wouldRunProjects").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Single(wouldRunProjects);
        Assert.Equal("tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj", wouldRunProjects[0]);

        var matrixAudit = root.GetProperty("matrices").GetProperty("test_matrix");
        Assert.Equal(2, matrixAudit.GetProperty("inputCount").GetInt32());
        Assert.Equal(1, matrixAudit.GetProperty("keptCount").GetInt32());
        Assert.Equal(1, matrixAudit.GetProperty("removedCount").GetInt32());
        Assert.Equal("audit", matrixAudit.GetProperty("mode").GetString());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PathNormalization()
    {
        var matrix = CreateMatrix(
            CreateEntry("ProjA-linux", "tests/ProjA/ProjA.csproj"));

        // Affected list uses backslashes
        var affected = JsonSerializer.Serialize(new[] { @"tests\ProjA\ProjA.csproj" });

        var result = await RunFilter(matrix, affected, runAll: false);

        result.EnsureSuccessful("Should handle backslash paths");
        var filtered = ParseOutputMatrix(result.Output, "test_matrix");
        Assert.Single(filtered);
    }

    private async Task<CommandResult> RunFilter(
        string matrixJson,
        string affectedProjects,
        bool runAll,
        bool auditOnly = false,
        string defaultCoverageProjects = "[]")
    {
        // Write a small wrapper script that calls filter-test-matrix-by-scope.ps1
        // with hashtable parameter (can't pass hashtable directly from CLI)
        var wrapperScript = Path.Combine(_tempDir.Path, "run-filter.ps1");
        var runAllSwitch = runAll ? "-RunAll" : "";
        var auditSwitch = auditOnly ? "-AuditOnly" : "";
        File.WriteAllText(wrapperScript, $$"""
            $matrices = @{
                'test_matrix' = '{{matrixJson.Replace("'", "''")}}'
            }
            & '{{_scriptPath}}' `
                -Matrices $matrices `
                -AffectedProjects '{{affectedProjects.Replace("'", "''")}}' `
                -DefaultCoverageProjects '{{defaultCoverageProjects.Replace("'", "''")}}' `
                {{runAllSwitch}} {{auditSwitch}}
            """);

        using var cmd = new PowerShellCommand(wrapperScript, _output)
            .WithTimeout(TimeSpan.FromMinutes(1));

        return await cmd.ExecuteAsync();
    }

    private async Task<(CommandResult Result, string GitHubOutputPath)> RunFilterWithGitHubOutput(
        string matrixJson,
        string affectedProjects,
        bool runAll,
        bool auditOnly = false)
    {
        var wrapperScript = Path.Combine(_tempDir.Path, "run-filter-github-output.ps1");
        var githubOutputPath = Path.Combine(_tempDir.Path, "github-output.txt");
        var runAllSwitch = runAll ? "-RunAll" : "";
        var auditSwitch = auditOnly ? "-AuditOnly" : "";
        File.WriteAllText(wrapperScript, $$"""
            $matrices = @{
                'test_matrix' = '{{matrixJson.Replace("'", "''")}}'
            }
            & '{{_scriptPath}}' `
                -Matrices $matrices `
                -AffectedProjects '{{affectedProjects.Replace("'", "''")}}' `
                {{runAllSwitch}} {{auditSwitch}} -OutputToGitHubEnv
            """);

        using var cmd = new PowerShellCommand(wrapperScript, _output)
            .WithEnvironmentVariable("GITHUB_OUTPUT", githubOutputPath)
            .WithTimeout(TimeSpan.FromMinutes(1));

        return (await cmd.ExecuteAsync(), githubOutputPath);
    }

    private async Task<(CommandResult Result, string GitHubOutputPath, string AuditPath)> RunFilterWithGitHubOutputAndAuditFile(
        string matrixJson,
        string affectedProjects,
        bool runAll,
        bool auditOnly = false)
    {
        var wrapperScript = Path.Combine(_tempDir.Path, "run-filter-github-output-audit.ps1");
        var githubOutputPath = Path.Combine(_tempDir.Path, "github-output.txt");
        var auditPath = Path.Combine(_tempDir.Path, "matrix-audit.json");
        var runAllSwitch = runAll ? "-RunAll" : "";
        var auditSwitch = auditOnly ? "-AuditOnly" : "";
        File.WriteAllText(wrapperScript, $$"""
            $matrices = @{
                'test_matrix' = '{{matrixJson.Replace("'", "''")}}'
            }
            & '{{_scriptPath}}' `
                -Matrices $matrices `
                -AffectedProjects '{{affectedProjects.Replace("'", "''")}}' `
                {{runAllSwitch}} {{auditSwitch}} `
                -AuditFilePath '{{auditPath.Replace("'", "''")}}' `
                -OutputToGitHubEnv
            """);

        using var cmd = new PowerShellCommand(wrapperScript, _output)
            .WithEnvironmentVariable("GITHUB_OUTPUT", githubOutputPath)
            .WithTimeout(TimeSpan.FromMinutes(1));

        return (await cmd.ExecuteAsync(), githubOutputPath, auditPath);
    }

    private static string CreateMatrix(params object[] entries)
    {
        var matrix = new { include = entries };
        return JsonSerializer.Serialize(matrix);
    }

    private static string CreateMatrixWithSingleIncludeObject(object entry)
    {
        var matrix = new Dictionary<string, object>
        {
            ["include"] = entry
        };

        return JsonSerializer.Serialize(matrix);
    }

    private static Dictionary<string, object> CreateEntry(string shortname, string testProjectPath, string? runsOn = null)
    {
        var entry = new Dictionary<string, object>
        {
            ["shortname"] = shortname,
            ["testProjectPath"] = testProjectPath,
            ["testSessionTimeout"] = "20m",
            ["testHangTimeout"] = "10m"
        };

        if (runsOn is not null)
        {
            entry["runs-on"] = runsOn;
        }

        return entry;
    }

    private static JsonElement[] ParseOutputMatrix(string output, string matrixName)
    {
        // Find the line with the matrix name followed by JSON content
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var prefix = $"{matrixName}: ";
            if (trimmed.StartsWith(prefix))
            {
                var rest = trimmed[prefix.Length..].Trim();
                // Only try to parse if it looks like JSON (starts with {)
                if (rest.StartsWith('{'))
                {
                    var parsed = JsonDocument.Parse(rest);
                    if (parsed.RootElement.TryGetProperty("include", out var include))
                    {
                        return include.EnumerateArray().ToArray();
                    }
                    return [];
                }
            }
        }
        return [];
    }

    private static string? GetGitHubOutputValue(string outputFilePath, string key)
    {
        foreach (var line in File.ReadAllLines(outputFilePath))
        {
            var prefix = $"{key}=";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..];
            }
        }

        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aspire.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find repository root");
    }
}
