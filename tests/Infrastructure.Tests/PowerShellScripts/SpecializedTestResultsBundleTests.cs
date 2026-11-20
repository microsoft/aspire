// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class SpecializedTestResultsBundleTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace;
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public SpecializedTestResultsBundleTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "create-specialized-test-results-bundle.ps1");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task BundlePreservesLaneAndReportsCurrentInheritedUnknownAndMissingResults()
    {
        const string runId = "12345";
        const int currentAttempt = 2;
        var inputDirectory = _workspace.CreateDirectory("downloaded-artifacts").FullName;
        var outputDirectory = Path.Combine(_workspace.Path, "bundle");
        var runsheetPath = Path.Combine(_workspace.Path, "runsheet.json");
        await File.WriteAllTextAsync(runsheetPath, JsonSerializer.Serialize(new[]
        {
            new { project = "Hosting.Tests", os = "ubuntu-latest" },
            new { project = "Dashboard.Tests", os = "windows-latest" },
            new { project = "Cli.Tests", os = "macos-latest" },
            new { project = "Missing.Tests", os = "ubuntu-latest" }
        }));

        CreateArtifact(inputDirectory, "logs-Hosting.Tests-ubuntu-latest", runId, 1, "inherited.trx");
        CreateArtifact(inputDirectory, "logs-Dashboard.Tests-windows-latest", runId, currentAttempt, "current.trx");
        CreateArtifact(inputDirectory, "logs-Cli.Tests-macos-latest", null, null, "unknown.trx");

        var result = await RunScript(inputDirectory, outputDirectory, runsheetPath, "success", runId, currentAttempt);

        result.EnsureSuccessful();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "manifest.json")));
        var lanes = manifest.RootElement.GetProperty("lanes").EnumerateArray().ToArray();

        Assert.Equal("success", manifest.RootElement.GetProperty("runsheetResult").GetString());
        Assert.Equal(4, manifest.RootElement.GetProperty("expectedLaneCount").GetInt32());
        Assert.Equal(3, manifest.RootElement.GetProperty("trxFileCount").GetInt32());
        Assert.Equal("inherited", GetLane(lanes, "Hosting.Tests").GetProperty("sourceStatus").GetString());
        Assert.Equal(1, GetLane(lanes, "Hosting.Tests").GetProperty("sourceAttempt").GetInt32());
        Assert.Equal("current", GetLane(lanes, "Dashboard.Tests").GetProperty("sourceStatus").GetString());
        Assert.Equal("unknown", GetLane(lanes, "Cli.Tests").GetProperty("sourceStatus").GetString());
        Assert.Equal("missing", GetLane(lanes, "Missing.Tests").GetProperty("sourceStatus").GetString());
        Assert.Equal("missing", GetLane(lanes, "Missing.Tests").GetProperty("trxStatus").GetString());

        var inheritedTrx = GetLane(lanes, "Hosting.Tests").GetProperty("trxFiles")[0].GetString()!;
        Assert.Contains("Hosting.Tests/ubuntu-latest/attempt-1/inherited.trx", inheritedTrx, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outputDirectory, inheritedTrx)));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task BundleMarksRunsheetFailureWhenNoLanesCouldBeRead()
    {
        var inputDirectory = Path.Combine(_workspace.Path, "no-artifacts");
        var outputDirectory = Path.Combine(_workspace.Path, "empty-bundle");
        var runsheetPath = Path.Combine(_workspace.Path, "missing-runsheet.json");
        await File.WriteAllTextAsync(runsheetPath, "");

        var result = await RunScript(inputDirectory, outputDirectory, runsheetPath, "failure", "12345", 1);

        result.EnsureSuccessful();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "manifest.json")));
        Assert.Equal("failure", manifest.RootElement.GetProperty("runsheetResult").GetString());
        Assert.Equal(0, manifest.RootElement.GetProperty("expectedLaneCount").GetInt32());
        Assert.Equal(0, manifest.RootElement.GetProperty("trxFileCount").GetInt32());
        Assert.Empty(manifest.RootElement.GetProperty("lanes").EnumerateArray());
    }

    private static JsonElement GetLane(JsonElement[] lanes, string project) =>
        lanes.Single(lane => lane.GetProperty("project").GetString() == project);

    private static void CreateArtifact(string inputDirectory, string artifactName, string? runId, int? attempt, string trxName)
    {
        var artifactDirectory = Directory.CreateDirectory(Path.Combine(inputDirectory, artifactName));
        if (runId is not null && attempt is not null)
        {
            File.WriteAllText(
                Path.Combine(artifactDirectory.FullName, "test-artifact-metadata.json"),
                JsonSerializer.Serialize(new { runId, runAttempt = attempt.Value }));
        }

        File.WriteAllText(Path.Combine(artifactDirectory.FullName, trxName), "<TestRun />");
    }

    private async Task<CommandResult> RunScript(
        string inputDirectory,
        string outputDirectory,
        string runsheetPath,
        string runsheetResult,
        string runId,
        int currentAttempt)
    {
        using var command = new PowerShellCommand(_scriptPath, _output);
        return await command.ExecuteAsync(
            "-InputDirectory", $"\"{inputDirectory}\"",
            "-OutputDirectory", $"\"{outputDirectory}\"",
            "-RunsheetPath", $"\"{runsheetPath}\"",
            "-RunsheetResult", runsheetResult,
            "-RunId", runId,
            "-CurrentAttempt", currentAttempt.ToString());
    }
}
