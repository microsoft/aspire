// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Commands;

public class PsCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task PsCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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

    [Theory]
    [InlineData("9.9.9", "9.9.9")]
    [InlineData("13.2.4.0", "13.2.4")]
    [InlineData("13.2.4.0+e7762a46d31842884a0bc72c92e07ba700c99bf5", "13.2.4")]
    [InlineData("13.3.0-pr.16502.g809f606f+e7762a46d31842884a0bc72c92e07ba700c99bf5", "13.3.0-pr.16502.g809f606f")]
    public async Task PsCommand_JsonFormat_NormalizesSdkVersionFromV2AppHostInfo(string sdkVersion, string expectedSdkVersion)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        using var server = TestAppHostBackchannelServer.Start(appHostPath, processId: 1234, sdkVersion: sdkVersion);
        using var connection = await server.ConnectAsync().DefaultTimeout();

        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        using var document = JsonDocument.Parse(jsonOutput);
        Assert.Equal(1, document.RootElement.GetArrayLength());
        Assert.Equal(expectedSdkVersion, document.RootElement[0].GetProperty("sdkVersion").GetString());
    }

    [Fact]
    public async Task PsCommand_JsonFormat_UsesNullSdkVersionWhenUnknown()
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
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        using var document = JsonDocument.Parse(jsonOutput);
        Assert.Equal(1, document.RootElement.GetArrayLength());
        Assert.True(document.RootElement[0].TryGetProperty("sdkVersion", out var sdkVersion));
        Assert.Equal(JsonValueKind.Null, sdkVersion.ValueKind);
    }

    [Fact]
    public async Task PsCommand_JsonFormat_DoesNotFetchSdkVersionFromV1Connection()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            SupportsV2 = false,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj"),
                ProcessId = 1234
            },
            AppHostInfoResponse = new GetAppHostInfoResponse
            {
                Pid = "1234",
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj"),
                AspireHostVersion = "9.9.9"
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        using var document = JsonDocument.Parse(jsonOutput);
        Assert.Equal(1, document.RootElement.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, document.RootElement[0].GetProperty("sdkVersion").ValueKind);
    }

    [Fact]
    public async Task PsCommand_JsonFormat_ReturnsAnonymousDashboardUrl()
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
            DashboardUrlsState = new DashboardUrlsState
            {
                BaseUrlWithLoginToken = "http://localhost:18888"
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);

        var appHosts = JsonSerializer.Deserialize(jsonOutput, PsCommandJsonContext.RelaxedEscaping.ListAppHostDisplayInfo);
        Assert.NotNull(appHosts);
        Assert.Single(appHosts);
        Assert.Equal("http://localhost:18888", appHosts[0].DashboardUrl);
    }

    [Fact]
    public async Task PsCommand_TableFormat_IncludesDashboardLoginTokenInDisplayedUrl()
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
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.DisableAnsi = true;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var output = string.Join(Environment.NewLine, textWriter.Logs);
        var normalizedOutput = output.Replace(Environment.NewLine, string.Empty, StringComparison.Ordinal);
        var expectedDashboardUrl = new Uri("http://localhost:18888/login?t=abc123").AbsoluteUri;
        Assert.Contains(expectedDashboardUrl, normalizedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PsCommand_TableFormat_IncludesSdkVersionFromV2AppHostInfo()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        using var server = TestAppHostBackchannelServer.Start(appHostPath, processId: 1234, sdkVersion: "13.2.4.0");
        using var connection = await server.ConnectAsync().DefaultTimeout();

        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.DisableAnsi = true;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var output = string.Join(Environment.NewLine, textWriter.Logs);
        var normalizedOutput = output.Replace(Environment.NewLine, string.Empty, StringComparison.Ordinal);
        Assert.Contains("SDK", normalizedOutput, StringComparison.Ordinal);
        Assert.Contains("13.2.4", normalizedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("13.2.4.0", normalizedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PsCommand_TableFormat_DisplaysDashWhenSdkVersionIsUnavailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            SupportsV2 = false,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = 1234
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.DisableAnsi = true;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ps");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var output = string.Join(Environment.NewLine, textWriter.Logs);
        Assert.Contains("SDK", output, StringComparison.Ordinal);
        Assert.Contains(" - ", output, StringComparison.Ordinal);
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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

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
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        // --resources with table format should not fetch resources
        var result = command.Parse("ps --resources");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(resourcesFetched, "Resources should not be fetched when output format is table");
    }

    private sealed class TestAppHostBackchannelServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TestAppHostRpcTarget _target;
        private readonly List<IDisposable> _disposables = [];

        private TestAppHostBackchannelServer(string appHostPath, int processId, string sdkVersion)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _target = new TestAppHostRpcTarget(appHostPath, processId, sdkVersion);
        }

        public static TestAppHostBackchannelServer Start(string appHostPath, int processId, string sdkVersion)
        {
            var server = new TestAppHostBackchannelServer(appHostPath, processId, sdkVersion);
            server._listener.Start();

            return server;
        }

        public async Task<AppHostAuxiliaryBackchannel> ConnectAsync()
        {
            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var acceptTask = _listener.AcceptSocketAsync();
            await clientSocket.ConnectAsync((IPEndPoint)_listener.LocalEndpoint).DefaultTimeout();
            var serverSocket = await acceptTask.DefaultTimeout();
            var serverStream = new NetworkStream(serverSocket, ownsSocket: true);
            var messageHandler = new HeaderDelimitedMessageHandler(serverStream, serverStream, BackchannelJsonSerializerContext.CreateRpcMessageFormatter());
            var rpc = new JsonRpc(messageHandler, _target);
            rpc.StartListening();
            _disposables.Add(rpc);
            _disposables.Add(messageHandler);
            _disposables.Add(serverStream);

            return await AppHostAuxiliaryBackchannel.CreateFromSocketAsync("hash1", "socket.hash1", isInScope: true, clientSocket).DefaultTimeout();
        }

        public void Dispose()
        {
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }

            _listener.Stop();
        }
    }

    private sealed class TestAppHostRpcTarget(string appHostPath, int processId, string sdkVersion)
    {
        public Task<AppHostInformation> GetAppHostInformationAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;

            return Task.FromResult(new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = processId
            });
        }

        public Task<GetCapabilitiesResponse> GetCapabilitiesAsync(GetCapabilitiesRequest? request = null, CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = cancellationToken;
            string[] capabilities = string.IsNullOrEmpty(sdkVersion)
                ? [AuxiliaryBackchannelCapabilities.V1]
                : [AuxiliaryBackchannelCapabilities.V1, AuxiliaryBackchannelCapabilities.V2];

            return Task.FromResult(new GetCapabilitiesResponse
            {
                Capabilities = capabilities
            });
        }

        public Task<GetAppHostInfoResponse> GetAppHostInfoAsync(GetAppHostInfoRequest? request = null, CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = cancellationToken;

            return Task.FromResult(new GetAppHostInfoResponse
            {
                Pid = processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AspireHostVersion = sdkVersion,
                AppHostPath = appHostPath
            });
        }

        public Task<DashboardUrlsState> GetDashboardUrlsAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;

            return Task.FromResult(new DashboardUrlsState
            {
                DashboardHealthy = !string.IsNullOrEmpty(appHostPath)
            });
        }
    }
}
