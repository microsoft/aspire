// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Templates.Tests;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for eng/scripts/split-test-matrix-by-deps.ps1
/// </summary>
public class SplitTestMatrixByDepsTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly string _githubOutputFile;
    private readonly ITestOutputHelper _output;

    public SplitTestMatrixByDepsTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(TestUtils.RepoRoot, "eng", "scripts", "split-test-matrix-by-deps.ps1");
        _githubOutputFile = Path.Combine(_tempDir.Path, "github_output.txt");
        File.WriteAllText(_githubOutputFile, "");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SplitsNoDepsTestsIntoNoNugetsBucket()
    {
        var matrixJson = BuildMatrixJson(
            new { name = "A", shortname = "a", runs_on = "ubuntu-latest" },
            new { name = "B", shortname = "b", runs_on = "ubuntu-latest" });

        var result = await RunScript(allTestsMatrix: matrixJson);

        result.EnsureSuccessful();

        var outputs = ParseGitHubOutputFile();
        Assert.Equal(2, outputs["tests_matrix_no_nugets"].Include.Length);
        Assert.Empty(outputs["tests_matrix_requires_nugets_linux"].Include);
        Assert.Empty(outputs["tests_matrix_requires_cli_archive"].Include);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SplitsRequiresNugetsTestsIntoRequiresNugetsBucket()
    {
        var matrixJson = BuildMatrixJson(
            new { name = "Plain", shortname = "p", runs_on = "ubuntu-latest" },
            new { name = "NugetTest", shortname = "nt", runs_on = "ubuntu-latest", properties = new { requiresNugets = true } });

        var result = await RunScript(allTestsMatrix: matrixJson);

        result.EnsureSuccessful();

        var outputs = ParseGitHubOutputFile();
        Assert.Single(outputs["tests_matrix_no_nugets"].Include);
        Assert.Equal("Plain", outputs["tests_matrix_no_nugets"].Include[0].Name);
        Assert.Single(outputs["tests_matrix_requires_nugets_linux"].Include);
        Assert.Equal("NugetTest", outputs["tests_matrix_requires_nugets_linux"].Include[0].Name);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SplitsRequiresNugetsByOs()
    {
        var matrixJson = BuildMatrixJson(
            new { name = "LinuxNuget", shortname = "ln", runs_on = "ubuntu-latest", properties = new { requiresNugets = true } },
            new { name = "WinNuget", shortname = "wn", runs_on = "windows-latest", properties = new { requiresNugets = true } },
            new { name = "MacNuget", shortname = "mn", runs_on = "macos-latest", properties = new { requiresNugets = true } });

        var result = await RunScript(allTestsMatrix: matrixJson);

        result.EnsureSuccessful();

        var outputs = ParseGitHubOutputFile();
        Assert.Single(outputs["tests_matrix_requires_nugets_linux"].Include);
        Assert.Equal("LinuxNuget", outputs["tests_matrix_requires_nugets_linux"].Include[0].Name);
        Assert.Single(outputs["tests_matrix_requires_nugets_windows"].Include);
        Assert.Equal("WinNuget", outputs["tests_matrix_requires_nugets_windows"].Include[0].Name);
        Assert.Single(outputs["tests_matrix_requires_nugets_macos"].Include);
        Assert.Equal("MacNuget", outputs["tests_matrix_requires_nugets_macos"].Include[0].Name);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SplitsRequiresCliArchiveTestsIntoCliArchiveBucket()
    {
        var matrixJson = BuildMatrixJson(
            new { name = "Plain", shortname = "p", runs_on = "ubuntu-latest" },
            new { name = "CliTest", shortname = "ct", runs_on = "ubuntu-latest", properties = new { requiresCliArchive = true } });

        var result = await RunScript(allTestsMatrix: matrixJson);

        result.EnsureSuccessful();

        var outputs = ParseGitHubOutputFile();
        Assert.Single(outputs["tests_matrix_no_nugets"].Include);
        Assert.Single(outputs["tests_matrix_requires_cli_archive"].Include);
        Assert.Equal("CliTest", outputs["tests_matrix_requires_cli_archive"].Include[0].Name);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task CliArchiveTakesPriorityOverNugets()
    {
        var matrixJson = BuildMatrixJson(
            new { name = "Both", shortname = "b", runs_on = "ubuntu-latest", properties = new { requiresNugets = true, requiresCliArchive = true } });

        var result = await RunScript(allTestsMatrix: matrixJson);

        result.EnsureSuccessful();

        var outputs = ParseGitHubOutputFile();
        Assert.Single(outputs["tests_matrix_requires_cli_archive"].Include);
        Assert.Empty(outputs["tests_matrix_requires_nugets_linux"].Include);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task OverflowsEntriesBeyondThreshold()
    {
        var entries = Enumerable.Range(1, 8).Select(i =>
            (object)new { name = $"T{i}", shortname = $"t{i}", runs_on = "ubuntu-latest" }).ToArray();

        var matrixJson = BuildMatrixJson(entries);

        var result = await RunScript(allTestsMatrix: matrixJson, overflowThreshold: 5);

        result.EnsureSuccessful();

        var outputs = ParseGitHubOutputFile();
        Assert.Equal(5, outputs["tests_matrix_no_nugets"].Include.Length);
        Assert.Equal(3, outputs["tests_matrix_no_nugets_overflow"].Include.Length);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task NoOverflowWhenBelowThreshold()
    {
        var entries = Enumerable.Range(1, 5).Select(i =>
            (object)new { name = $"T{i}", shortname = $"t{i}", runs_on = "ubuntu-latest" }).ToArray();

        var matrixJson = BuildMatrixJson(entries);

        var result = await RunScript(allTestsMatrix: matrixJson, overflowThreshold: 10);

        result.EnsureSuccessful();

        var outputs = ParseGitHubOutputFile();
        Assert.Equal(5, outputs["tests_matrix_no_nugets"].Include.Length);
        Assert.Empty(outputs["tests_matrix_no_nugets_overflow"].Include);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task HandlesEmptyMatrix()
    {
        var matrixJson = BuildMatrixJson();

        var result = await RunScript(allTestsMatrix: matrixJson);

        result.EnsureSuccessful();

        var outputs = ParseGitHubOutputFile();
        Assert.Empty(outputs["tests_matrix_no_nugets"].Include);
        Assert.Empty(outputs["tests_matrix_no_nugets_overflow"].Include);
        Assert.Empty(outputs["tests_matrix_requires_nugets_linux"].Include);
        Assert.Empty(outputs["tests_matrix_requires_nugets_windows"].Include);
        Assert.Empty(outputs["tests_matrix_requires_nugets_macos"].Include);
        Assert.Empty(outputs["tests_matrix_requires_cli_archive"].Include);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task AllOutputKeysAlwaysPresent()
    {
        var matrixJson = BuildMatrixJson(
            new { name = "T", shortname = "t", runs_on = "ubuntu-latest" });

        var result = await RunScript(allTestsMatrix: matrixJson);

        result.EnsureSuccessful();

        var outputs = ParseGitHubOutputFile();
        Assert.True(outputs.ContainsKey("tests_matrix_no_nugets"));
        Assert.True(outputs.ContainsKey("tests_matrix_no_nugets_overflow"));
        Assert.True(outputs.ContainsKey("tests_matrix_requires_nugets_linux"));
        Assert.True(outputs.ContainsKey("tests_matrix_requires_nugets_windows"));
        Assert.True(outputs.ContainsKey("tests_matrix_requires_nugets_macos"));
        Assert.True(outputs.ContainsKey("tests_matrix_requires_cli_archive"));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task ReadsMatrixFromFile()
    {
        var matrixJson = BuildMatrixJson(
            new { name = "FromFile", shortname = "ff", runs_on = "ubuntu-latest" });

        var matrixFile = Path.Combine(_tempDir.Path, "matrix.json");
        File.WriteAllText(matrixFile, matrixJson);

        var result = await RunScript(allTestsMatrixFile: matrixFile);

        result.EnsureSuccessful();

        var outputs = ParseGitHubOutputFile();
        Assert.Single(outputs["tests_matrix_no_nugets"].Include);
        Assert.Equal("FromFile", outputs["tests_matrix_no_nugets"].Include[0].Name);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenNoInputProvided()
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithEnvironmentVariable("GITHUB_OUTPUT", _githubOutputFile);

        var result = await cmd.ExecuteAsync("-OutputToGitHubEnv");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenMatrixFileNotFound()
    {
        var nonExistentFile = Path.Combine(_tempDir.Path, "nonexistent.json");

        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithEnvironmentVariable("GITHUB_OUTPUT", _githubOutputFile);

        var result = await cmd.ExecuteAsync(
            "-AllTestsMatrixFile", $"\"{nonExistentFile}\"",
            "-OutputToGitHubEnv");

        Assert.NotEqual(0, result.ExitCode);
    }

    private async Task<CommandResult> RunScript(
        string? allTestsMatrix = null,
        string? allTestsMatrixFile = null,
        int? overflowThreshold = null)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithEnvironmentVariable("GITHUB_OUTPUT", _githubOutputFile);

        var args = new List<string>();

        if (!string.IsNullOrEmpty(allTestsMatrix))
        {
            // Write JSON to a temp file to avoid command-line quoting issues
            var tempMatrixFile = Path.Combine(_tempDir.Path, $"matrix_input_{Guid.NewGuid():N}.json");
            File.WriteAllText(tempMatrixFile, allTestsMatrix);
            args.Add("-AllTestsMatrixFile");
            args.Add($"\"{tempMatrixFile}\"");
        }

        if (!string.IsNullOrEmpty(allTestsMatrixFile))
        {
            args.Add("-AllTestsMatrixFile");
            args.Add($"\"{allTestsMatrixFile}\"");
        }

        if (overflowThreshold.HasValue)
        {
            args.Add("-OverflowThreshold");
            args.Add(overflowThreshold.Value.ToString());
        }

        args.Add("-OutputToGitHubEnv");

        return await cmd.ExecuteAsync(args.ToArray());
    }

    private static string BuildMatrixJson(params object[] entries)
    {
        // Use dictionaries to produce JSON with the exact property names the PS script expects.
        // In particular, 'runs-on' requires a hyphen which C# identifiers cannot express.
        var convertedEntries = new List<Dictionary<string, object>>();
        foreach (var entry in entries)
        {
            var dict = new Dictionary<string, object>();
            foreach (var prop in entry.GetType().GetProperties())
            {
                var key = prop.Name == "runs_on" ? "runs-on" : prop.Name;
                dict[key] = prop.GetValue(entry)!;
            }
            convertedEntries.Add(dict);
        }

        var matrix = new { include = convertedEntries };
        return JsonSerializer.Serialize(matrix);
    }

    private Dictionary<string, GitHubActionsMatrix> ParseGitHubOutputFile()
    {
        var results = new Dictionary<string, GitHubActionsMatrix>();
        foreach (var line in File.ReadAllLines(_githubOutputFile))
        {
            var eqIndex = line.IndexOf('=');
            if (eqIndex < 0)
            {
                continue;
            }

            var key = line[..eqIndex];
            var value = line[(eqIndex + 1)..];
            results[key] = ParseGitHubMatrixJson(value);
        }
        return results;
    }

    private static GitHubActionsMatrix ParseGitHubMatrixJson(string json)
    {
        var document = JsonDocument.Parse(json);
        var include = document.RootElement.GetProperty("include");

        ExpandedMatrixEntry[] entries;
        if (include.ValueKind == JsonValueKind.Array)
        {
            entries = JsonSerializer.Deserialize<ExpandedMatrixEntry[]>(include.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }
        else if (include.ValueKind == JsonValueKind.Object)
        {
            var singleEntry = JsonSerializer.Deserialize<ExpandedMatrixEntry>(include.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            entries = singleEntry is not null ? [singleEntry] : [];
        }
        else
        {
            entries = [];
        }

        return new GitHubActionsMatrix { Include = entries };
    }

}
