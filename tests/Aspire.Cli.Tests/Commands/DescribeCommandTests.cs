// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Runtime.CompilerServices;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
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
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
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

    [Fact]
    public async Task DescribeCommand_Follow_WhenBackchannelIsDisposed_ExitsSuccessfully()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var errorWriter = new StringWriter();
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
        ], configureConnection: connection =>
        {
            connection.AppHostInfo = CreateAppHostInfo(workspace, Environment.ProcessId);
            connection.WatchResourceSnapshotsHandler = static (_, cancellationToken) => ThrowObjectDisposedAfterSnapshot(cancellationToken);
        }, errorTextWriter: errorWriter);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --follow --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonLines = outputWriter.Logs
            .Where(l => l.TrimStart().StartsWith("{", StringComparison.Ordinal))
            .ToList();

        Assert.Single(jsonLines);
        Assert.DoesNotContain(outputWriter.Logs, l => l.Contains("unexpected error occurred", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(InteractionServiceStrings.AppHostConnectionLostGeneric, errorWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeCommand_Follow_WhenAppHostHasExited_WritesShutdownMessageToStderr()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var errorWriter = new StringWriter();
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
        ], configureConnection: connection =>
        {
            connection.AppHostInfo = CreateAppHostInfo(workspace, int.MaxValue);
            connection.WatchResourceSnapshotsHandler = static (_, cancellationToken) => ThrowObjectDisposedAfterSnapshot(cancellationToken);
        }, errorTextWriter: errorWriter);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --follow --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Single(outputWriter.Logs, l => l.TrimStart().StartsWith("{", StringComparison.Ordinal));
        Assert.Contains(InteractionServiceStrings.AppHostShutDown, errorWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeCommand_Follow_WhenCanceledAndBackchannelIsDisposed_DoesNotWriteStatusToStderr()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var errorWriter = new StringWriter();
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
        ], configureConnection: connection =>
        {
            connection.AppHostInfo = CreateAppHostInfo(workspace, Environment.ProcessId);
            connection.WatchResourceSnapshotsHandler = static (_, cancellationToken) => ThrowObjectDisposedAfterCancellationAsync(cancellationToken);
        }, errorTextWriter: errorWriter);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --follow --format json");

        using var cts = new CancellationTokenSource();
        var pendingRun = result.InvokeAsync(cancellationToken: cts.Token);
        await Task.Yield();
        cts.Cancel();

        var exitCode = await pendingRun.DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.DoesNotContain(InteractionServiceStrings.AppHostConnectionLostGeneric, errorWriter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(InteractionServiceStrings.AppHostShutDown, errorWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeCommand_JsonFormat_StripsLoginPathFromDashboardUrl()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
        ], dashboardUrlsState: new DashboardUrlsState
        {
            BaseUrlWithLoginToken = "http://localhost:18888/login?t=abcd1234"
        });

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join("", outputWriter.Logs);
        var deserialized = JsonSerializer.Deserialize(jsonOutput, ResourcesCommandJsonContext.RelaxedEscaping.ResourcesOutput);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Resources);

        Assert.Equal("http://localhost:18888/?resource=redis", deserialized.Resources[0].DashboardUrl);
    }

    [Fact]
    public async Task DescribeCommand_Follow_JsonFormat_StripsLoginPathFromDashboardUrl()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
        ], dashboardUrlsState: new DashboardUrlsState
        {
            BaseUrlWithLoginToken = "http://localhost:18888/login?t=abcd1234"
        });

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --follow --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonLines = outputWriter.Logs
            .Where(l => l.TrimStart().StartsWith("{", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(jsonLines);

        var resource = JsonSerializer.Deserialize(jsonLines[0], ResourcesCommandJsonContext.Ndjson.ResourceJson);
        Assert.NotNull(resource);

        Assert.Equal("http://localhost:18888/?resource=redis", resource.DashboardUrl);
    }

    [Fact]
    public async Task DescribeCommand_HiddenResources_AreExcludedByDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            new ResourceSnapshot { Name = "aspire-dashboard", DisplayName = "aspire-dashboard", ResourceType = "Executable", State = "Hidden" },
            new ResourceSnapshot { Name = "hidden-svc", DisplayName = "hidden-svc", ResourceType = "Project", State = "Running", IsHidden = true },
        ]);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join("", outputWriter.Logs);
        var deserialized = JsonSerializer.Deserialize(jsonOutput, ResourcesCommandJsonContext.RelaxedEscaping.ResourcesOutput);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Resources);
        Assert.Equal("redis", deserialized.Resources[0].Name);
    }

    [Fact]
    public async Task DescribeCommand_IncludeHidden_ShowsHiddenResources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            new ResourceSnapshot { Name = "aspire-dashboard", DisplayName = "aspire-dashboard", ResourceType = "Executable", State = "Hidden" },
            new ResourceSnapshot { Name = "hidden-svc", DisplayName = "hidden-svc", ResourceType = "Project", State = "Running", IsHidden = true },
        ]);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --format json --include-hidden");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join("", outputWriter.Logs);
        var deserialized = JsonSerializer.Deserialize(jsonOutput, ResourcesCommandJsonContext.RelaxedEscaping.ResourcesOutput);

        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized.Resources.Length);
        Assert.Contains(deserialized.Resources, r => r.Name == "redis");
        Assert.Contains(deserialized.Resources, r => r.Name == "aspire-dashboard");
        Assert.Contains(deserialized.Resources, r => r.Name == "hidden-svc");
    }

    [Fact]
    public async Task DescribeCommand_SpecificResource_IncludesHiddenWithoutFlag()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            new ResourceSnapshot { Name = "aspire-dashboard", DisplayName = "aspire-dashboard", ResourceType = "Executable", State = "Hidden" },
        ]);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe aspire-dashboard --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join("", outputWriter.Logs);
        var deserialized = JsonSerializer.Deserialize(jsonOutput, ResourcesCommandJsonContext.RelaxedEscaping.ResourcesOutput);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Resources);
        Assert.Equal("aspire-dashboard", deserialized.Resources[0].Name);
    }

    [Fact]
    public async Task DescribeCommand_Follow_HiddenResources_AreExcludedByDefault()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            new ResourceSnapshot { Name = "aspire-dashboard", DisplayName = "aspire-dashboard", ResourceType = "Executable", State = "Hidden" },
        ]);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --follow --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonLines = outputWriter.Logs
            .Where(l => l.TrimStart().StartsWith("{", StringComparison.Ordinal))
            .ToList();

        Assert.Single(jsonLines);

        var resource = JsonSerializer.Deserialize(jsonLines[0], ResourcesCommandJsonContext.Ndjson.ResourceJson);
        Assert.NotNull(resource);
        Assert.Equal("redis", resource.Name);
    }

    [Fact]
    public async Task DescribeCommand_Follow_IncludeHidden_ShowsHiddenResources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var provider = CreateDescribeTestServices(workspace, outputWriter, [
            new ResourceSnapshot { Name = "redis", DisplayName = "redis", ResourceType = "Container", State = "Running" },
            new ResourceSnapshot { Name = "aspire-dashboard", DisplayName = "aspire-dashboard", ResourceType = "Executable", State = "Hidden" },
        ]);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("describe --follow --format json --include-hidden");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonLines = outputWriter.Logs
            .Where(l => l.TrimStart().StartsWith("{", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, jsonLines.Count);
        Assert.Contains(jsonLines, l => l.Contains("redis"));
        Assert.Contains(jsonLines, l => l.Contains("aspire-dashboard"));
    }

    private ServiceProvider CreateDescribeTestServices(
        TemporaryWorkspace workspace,
        TestOutputTextWriter outputWriter,
        List<ResourceSnapshot> resourceSnapshots,
        bool disableAnsi = false,
        DashboardUrlsState? dashboardUrlsState = null,
        Action<TestAppHostAuxiliaryBackchannel>? configureConnection = null,
        StringWriter? errorTextWriter = null)
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
            ResourceSnapshots = resourceSnapshots,
            DashboardUrlsState = dashboardUrlsState
        };
        configureConnection?.Invoke(connection);
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.OutputTextWriter = outputWriter;
            options.ErrorTextWriter = errorTextWriter;
            options.DisableAnsi = disableAnsi;
        });

        return services.BuildServiceProvider();
    }

    private static AppHostInformation CreateAppHostInfo(TemporaryWorkspace workspace, int processId)
    {
        return new AppHostInformation
        {
            AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
            ProcessId = processId
        };
    }

    private static async IAsyncEnumerable<ResourceSnapshot> ThrowObjectDisposedAfterSnapshot([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ResourceSnapshot
        {
            Name = "redis",
            DisplayName = "redis",
            ResourceType = "Container",
            State = "Running"
        };

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        throw new ObjectDisposedException("StreamJsonRpc.JsonRpc");
    }

    private static async IAsyncEnumerable<ResourceSnapshot> ThrowObjectDisposedAfterCancellationAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ResourceSnapshot
        {
            Name = "redis",
            DisplayName = "redis",
            ResourceType = "Container",
            State = "Running"
        };

        var waitForCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => waitForCancellation.TrySetResult());
        await waitForCancellation.Task;

        throw new ObjectDisposedException("StreamJsonRpc.JsonRpc");
    }
}
