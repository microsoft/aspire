// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class PsCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task PsCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task PsCommand_WhenNoAppHostRunning_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // ps should succeed even with no running AppHosts (just shows empty list)
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("Json")]
    [InlineData("JSON")]
    public async Task PsCommand_FormatOption_IsCaseInsensitive(string format)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"ps --format {format}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("Table")]
    [InlineData("TABLE")]
    public async Task PsCommand_FormatOption_AcceptsTable(string format)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"ps --format {format}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task PsCommand_FormatOption_RejectsInvalidValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps --format invalid");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task PsCommand_JsonFormat_ReturnsValidJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection1 = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj"),
                ProcessId = 1234,
                CliProcessId = 5678
            },
            DashboardUrlsState = new DashboardUrlsState
            {
                BaseUrlWithLoginToken = "http://localhost:18888/login?t=abc123"
            }
        };
        var connection2 = new TestAppHostAuxiliaryBackchannel
        {
            Hash = "test-hash-2",
            SocketPath = "/tmp/test2.sock",
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "App2.AppHost.csproj"),
                ProcessId = 9012
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection1);
        monitor.AddConnection("hash2", "socket.hash2", connection2);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);

        var appHosts = JsonSerializer.Deserialize(jsonOutput, PsCommandJsonContext.RelaxedEscaping.ListAppHostDisplayInfo);
        Assert.NotNull(appHosts);

        Assert.Collection(appHosts.OrderBy(a => a.AppHostPid),
            first =>
            {
                Assert.EndsWith("App1.AppHost.csproj", first.AppHostPath);
                Assert.Equal(1234, first.AppHostPid);
                Assert.Equal(5678, first.CliPid);
                Assert.Equal("http://localhost:18888/login?t=abc123", first.DashboardUrl);
            },
            second =>
            {
                Assert.EndsWith("App2.AppHost.csproj", second.AppHostPath);
                Assert.Equal(9012, second.AppHostPid);
                Assert.Null(second.CliPid);
                Assert.Null(second.DashboardUrl);
            });
    }

    [Fact]
    public async Task PsCommand_JsonFormat_NoResults_WritesEmptyArrayToStdout()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var json = string.Join(string.Empty, textWriter.Logs);
        var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(0, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task PsCommand_ResourcesOption_IncludesResourcesInJsonOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj"),
                ProcessId = 1234,
                CliProcessId = 5678
            },
            DashboardUrlsState = new DashboardUrlsState
            {
                BaseUrlWithLoginToken = "http://localhost:18888/login?t=abc123"
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "apiservice",
                    DisplayName = "apiservice",
                    ResourceType = "Project",
                    State = "Running",
                    StateStyle = "success",
                    Urls =
                    [
                        new ResourceSnapshotUrl { Name = "https", Url = "https://localhost:7001" }
                    ]
                },
                new ResourceSnapshot
                {
                    Name = "redis",
                    DisplayName = "redis",
                    ResourceType = "Container",
                    State = "Running",
                    StateStyle = "success"
                }
            ]
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps --format json --resources");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        var appHosts = JsonSerializer.Deserialize(jsonOutput, PsCommandJsonContext.RelaxedEscaping.ListAppHostDisplayInfo);
        Assert.NotNull(appHosts);
        Assert.Single(appHosts);

        var appHost = appHosts[0];
        Assert.NotNull(appHost.Resources);
        Assert.Equal(2, appHost.Resources.Count);

        var apiService = appHost.Resources.First(r => r.Name == "apiservice");
        Assert.Equal("Project", apiService.ResourceType);
        Assert.Equal("Running", apiService.State);
        Assert.NotNull(apiService.Urls);
        Assert.Single(apiService.Urls);
        Assert.Equal("https://localhost:7001", apiService.Urls[0].Url);

        var redis = appHost.Resources.First(r => r.Name == "redis");
        Assert.Equal("Container", redis.ResourceType);
        Assert.Equal("Running", redis.State);
    }

    [Fact]
    public async Task PsCommand_WithoutResourcesOption_OmitsResourcesFromJsonOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj"),
                ProcessId = 1234
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "apiservice",
                    ResourceType = "Project",
                    State = "Running"
                }
            ]
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        var appHosts = JsonSerializer.Deserialize(jsonOutput, PsCommandJsonContext.RelaxedEscaping.ListAppHostDisplayInfo);
        Assert.NotNull(appHosts);
        Assert.Single(appHosts);
        Assert.Null(appHosts[0].Resources);

        // Also verify the raw JSON doesn't contain a "resources" key
        var document = JsonDocument.Parse(jsonOutput);
        var firstElement = document.RootElement[0];
        Assert.False(firstElement.TryGetProperty("resources", out _));
    }

    [Fact]
    public async Task PsCommand_ResourcesOption_TableFormat_DoesNotFetchResources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);

        var resourcesFetched = false;
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj"),
                ProcessId = 1234
            },
            GetResourceSnapshotsHandler = _ =>
            {
                resourcesFetched = true;
                return Task.FromResult(new List<ResourceSnapshot>
                {
                    new ResourceSnapshot { Name = "apiservice", ResourceType = "Project", State = "Running" }
                });
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // --resources with table format should not fetch resources
        var result = command.Parse("ps --resources");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(resourcesFetched, "Resources should not be fetched when output format is table");
    }
}
