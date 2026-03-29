// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Pipelines.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class ContinuationStateManagerTests : IDisposable
{
    private readonly string _tempDir;

    public ContinuationStateManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aspire-state-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task AcquireSection_EmptyDirectory_ReturnsEmptySection()
    {
        var manager = CreateManager("run-1", "build");

        var section = await manager.AcquireSectionAsync("MySection");

        Assert.NotNull(section);
        Assert.Equal("MySection", section.SectionName);
        Assert.Empty(section.Data);
    }

    [Fact]
    public async Task SaveAndAcquire_RoundTrips()
    {
        var manager = CreateManager("run-1", "build");

        var section = await manager.AcquireSectionAsync("Config");
        section.Data["key1"] = JsonValue.Create("value1");
        await manager.SaveSectionAsync(section);

        var restored = await manager.AcquireSectionAsync("Config");
        Assert.Equal("value1", restored.Data["key1"]?.GetValue<string>());
    }

    [Fact]
    public async Task WritesOnlyToScopeFile()
    {
        var manager = CreateManager("run-1", "build");

        var section = await manager.AcquireSectionAsync("Data");
        section.Data["x"] = JsonValue.Create("y");
        await manager.SaveSectionAsync(section);

        var expectedFile = Path.Combine(_tempDir, "run-1", "build.json");
        Assert.True(File.Exists(expectedFile));

        // Only one file should exist in the run directory
        var files = Directory.GetFiles(Path.Combine(_tempDir, "run-1"), "*.json");
        Assert.Single(files);
    }

    [Fact]
    public async Task MergesMultipleScopeFiles()
    {
        var runDir = Path.Combine(_tempDir, "run-1");
        Directory.CreateDirectory(runDir);

        // Write state from "build" scope
        var buildState = new JsonObject { ["BuildOutput"] = new JsonObject { ["artifact"] = JsonValue.Create("app.zip") } };
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "build.json"),
            JsonFlattener.FlattenJsonObject(buildState).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // Write state from "test" scope
        var testState = new JsonObject { ["TestResults"] = new JsonObject { ["passed"] = JsonValue.Create("42") } };
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "test.json"),
            JsonFlattener.FlattenJsonObject(testState).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // Now read as "deploy" scope — should see merged state
        var deployManager = CreateManager("run-1", "deploy");

        var buildSection = await deployManager.AcquireSectionAsync("BuildOutput");
        Assert.Equal("app.zip", buildSection.Data["artifact"]?.GetValue<string>());

        var testSection = await deployManager.AcquireSectionAsync("TestResults");
        Assert.Equal("42", testSection.Data["passed"]?.GetValue<string>());
    }

    [Fact]
    public async Task ConcurrentRunsDontInterfere()
    {
        // Run 1 writes "build" scope
        var run1Manager = CreateManager("run-1", "build");
        var section1 = await run1Manager.AcquireSectionAsync("Data");
        section1.Data["from"] = JsonValue.Create("run1");
        await run1Manager.SaveSectionAsync(section1);

        // Run 2 writes "build" scope (same job name, different run)
        var run2Manager = CreateManager("run-2", "build");
        var section2 = await run2Manager.AcquireSectionAsync("Data");
        section2.Data["from"] = JsonValue.Create("run2");
        await run2Manager.SaveSectionAsync(section2);

        // Verify isolation: each run's state is in its own directory
        var run1File = Path.Combine(_tempDir, "run-1", "build.json");
        var run2File = Path.Combine(_tempDir, "run-2", "build.json");
        Assert.True(File.Exists(run1File));
        Assert.True(File.Exists(run2File));

        // Read back run 1 — should see "run1"
        var run1ReadManager = CreateManager("run-1", "deploy");
        var run1Section = await run1ReadManager.AcquireSectionAsync("Data");
        Assert.Equal("run1", run1Section.Data["from"]?.GetValue<string>());

        // Read back run 2 — should see "run2"
        var run2ReadManager = CreateManager("run-2", "deploy");
        var run2Section = await run2ReadManager.AcquireSectionAsync("Data");
        Assert.Equal("run2", run2Section.Data["from"]?.GetValue<string>());
    }

    [Fact]
    public async Task DeleteSection_RemovesSectionFromState()
    {
        var manager = CreateManager("run-1", "build");

        var section = await manager.AcquireSectionAsync("ToDelete");
        section.Data["key"] = JsonValue.Create("val");
        await manager.SaveSectionAsync(section);

        // Verify it's there
        var check = await manager.AcquireSectionAsync("ToDelete");
        Assert.NotEmpty(check.Data);

        // Delete it
        await manager.DeleteSectionAsync(check);

        // Re-read — should be empty
        var afterDelete = await manager.AcquireSectionAsync("ToDelete");
        Assert.Empty(afterDelete.Data);
    }

    [Fact]
    public void StateFilePath_ReturnsWritePath()
    {
        var manager = CreateManager("run-42", "build");

        Assert.Equal(Path.Combine(_tempDir, "run-42", "build.json"), manager.StateFilePath);
    }

    private ContinuationStateManager CreateManager(string runId, string jobId)
    {
        return new ContinuationStateManager(
            NullLogger<ContinuationStateManager>.Instance,
            new PipelineScopeResult { RunId = runId, JobId = jobId },
            _tempDir);
    }
}
