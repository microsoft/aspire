// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class DescribeCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DescribeCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --help");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task DescribeCommand_WhenNoAppHostRunning_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Should succeed - no running AppHost is not an error (like Unix ps with no processes)
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("Json")]
    [InlineData("JSON")]
    public async Task DescribeCommand_FormatOption_IsCaseInsensitive(string format)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"describe --format {format} --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("Table")]
    [InlineData("TABLE")]
    public async Task DescribeCommand_FormatOption_AcceptsTable(string format)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"describe --format {format} --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task DescribeCommand_FormatOption_RejectsInvalidValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --format invalid");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task DescribeCommand_FollowOption_CanBeParsed()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --follow --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task DescribeCommand_LegacyWatchOption_StillWorks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --watch --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task DescribeCommand_LegacyResourcesAlias_StillWorks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resources --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task DescribeCommand_FollowAndFormat_CanBeCombined()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --follow --format json --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task DescribeCommand_ResourceNameArgument_CanBeParsed()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe myresource --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task DescribeCommand_AllOptions_CanBeCombined()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe myresource --follow --format json --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public void DescribeCommand_NdjsonFormat_OutputsOneObjectPerLine()
    {
        // Arrange - create resource JSON objects
        var resources = new[]
        {
            new ResourceJson { Name = "frontend", DisplayName = "frontend", ResourceType = "Project", State = "Running" },
            new ResourceJson { Name = "postgres", DisplayName = "postgres", ResourceType = "Container", State = "Running" },
            new ResourceJson { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Starting" }
        };

        // Act - serialize each resource separately (simulating NDJSON streaming output for --follow)
        var ndjsonLines = resources
            .Select(r => JsonSerializer.Serialize(r, ResourcesCommandJsonContext.Ndjson.ResourceJson))
            .ToList();

        // Assert - each line is a complete, valid JSON object with no internal newlines
        foreach (var line in ndjsonLines)
        {
            // Verify no newlines within the JSON (compact format)
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);

            // Verify it's valid JSON that can be deserialized
            var deserialized = JsonSerializer.Deserialize(line, ResourcesCommandJsonContext.Ndjson.ResourceJson);
            Assert.NotNull(deserialized);
        }

        // Verify NDJSON format: joining with newlines creates parseable multi-line output
        var ndjsonOutput = string.Join('\n', ndjsonLines);
        var parsedLines = ndjsonOutput.Split('\n')
            .Select(line => JsonSerializer.Deserialize(line, ResourcesCommandJsonContext.Ndjson.ResourceJson))
            .ToList();

        Assert.Equal(3, parsedLines.Count);
        Assert.Equal("frontend", parsedLines[0]!.Name);
        Assert.Equal("postgres", parsedLines[1]!.Name);
        Assert.Equal("Starting", parsedLines[2]!.State);
    }

    [Fact]
    public void DescribeCommand_SnapshotFormat_OutputsWrappedJsonArray()
    {
        // Arrange - resources output for snapshot
        var resourcesOutput = new ResourcesOutput
        {
            Resources =
            [
                new ResourceJson { Name = "frontend", DisplayName = "frontend", ResourceType = "Project", State = "Running" },
                new ResourceJson { Name = "postgres", DisplayName = "postgres", ResourceType = "Container", State = "Running" }
            ]
        };

        // Act - serialize as snapshot (wrapped JSON)
        var json = JsonSerializer.Serialize(resourcesOutput, ResourcesCommandJsonContext.RelaxedEscaping.ResourcesOutput);

        // Assert - it's a single JSON object with "resources" array
        Assert.Contains("\"resources\"", json);
        Assert.StartsWith("{", json.TrimStart());
        Assert.EndsWith("}", json.TrimEnd());

        // Verify it can be deserialized back
        var deserialized = JsonSerializer.Deserialize(json, ResourcesCommandJsonContext.RelaxedEscaping.ResourcesOutput);
        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Resources.Length);
        Assert.Equal("frontend", deserialized.Resources[0].Name);
    }

    [Fact]
    public async Task DescribeCommand_Follow_JsonFormat_DeduplicatesIdenticalSnapshots()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            // Duplicate - identical to the first snapshot
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            // Changed state - should be emitted
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Stopping" },
            // Duplicate of the changed state - should be suppressed
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Stopping" },
        ]);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --follow --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Parse all JSON lines from output
        var jsonLines = outputWriter.Logs
            .Where(l => l.TrimStart().StartsWith("{", StringComparison.Ordinal))
            .ToList();

        // Should only have 2 lines: the initial "Running" snapshot and the "Stopping" change.
        // The duplicate "Running" and duplicate "Stopping" snapshots should be suppressed.
        Assert.Equal(2, jsonLines.Count);

        var first = JsonSerializer.Deserialize(jsonLines[0], ResourcesCommandJsonContext.Ndjson.ResourceJson);
        Assert.NotNull(first);
        Assert.Equal("redis", first.Name);
        Assert.Equal("Container", first.ResourceType);
        Assert.Equal("Running", first.State);

        var second = JsonSerializer.Deserialize(jsonLines[1], ResourcesCommandJsonContext.Ndjson.ResourceJson);
        Assert.NotNull(second);
        Assert.Equal("redis", second.Name);
        Assert.Equal("Container", second.ResourceType);
        Assert.Equal("Stopping", second.State);
    }

    [Fact]
    public async Task DescribeCommand_Follow_TableFormat_DeduplicatesIdenticalSnapshots()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            // Duplicate - identical to the first snapshot
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            // Changed state - should be emitted
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Stopping" },
            // Duplicate of the changed state - should be suppressed
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Stopping" },
        ], disableAnsi: true);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --follow");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Filter to lines containing the resource name indicator
        var resourceLines = outputWriter.Logs
            .Where(l => l.StartsWith("[redis]", StringComparison.Ordinal))
            .ToList();

        // Should only have 2 lines: one for "Running" and one for "Stopping".
        // Duplicate snapshots with the same state should be suppressed.
        Assert.Equal(2, resourceLines.Count);
        Assert.Equal("[redis] Running", resourceLines[0]);
        Assert.Equal("[redis] Stopping", resourceLines[1]);
    }

    private ServiceProvider CreateDescribeTestServices(
        TemporaryWorkspace workspace,
        TestOutputTextWriter outputWriter,
        List<ResourceSnapshot> resourceSnapshots,
        bool disableAnsi = false)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 1234
            },
            ResourceSnapshots = resourceSnapshots
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = disableAnsi;
        });

        return services.BuildServiceProvider();
    }
}
