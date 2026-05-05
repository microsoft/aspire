// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class TerminalCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task TerminalCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal --help");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task TerminalCommand_WhenNoResourceArgument_FailsParsing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task TerminalCommand_WhenNoAppHostRunning_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal myresource");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Mirrors the LogsCommand behavior: no running AppHost is informational, not an error.
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task TerminalCommand_WhenAppHostLacksTerminalsV1Capability_ReturnsAppHostIncompatible()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var (provider, _) = CreateProviderWithBackchannel(
            workspace,
            backchannel =>
            {
                backchannel.SupportsTerminalsV1 = false;
            });
        using (provider)
        {
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("terminal myresource");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(ExitCodeConstants.AppHostIncompatible, exitCode);
        }
    }

    [Fact]
    public async Task TerminalCommand_WhenResourceNotFound_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var (provider, _) = CreateProviderWithBackchannel(
            workspace,
            backchannel =>
            {
                backchannel.ResourceSnapshots = [];
            });
        using (provider)
        {
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("terminal does-not-exist");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        }
    }

    [Fact]
    public async Task TerminalCommand_WhenTerminalNotAvailable_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var (provider, _) = CreateProviderWithBackchannel(
            workspace,
            backchannel =>
            {
                backchannel.ResourceSnapshots = [CreateSnapshot("myresource")];
                backchannel.TerminalInfoResponse = new GetTerminalInfoResponse
                {
                    IsAvailable = false,
                    Replicas = null
                };
            });
        using (provider)
        {
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("terminal myresource");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        }
    }

    [Fact]
    public async Task TerminalCommand_WhenReplicasArrayEmpty_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var (provider, _) = CreateProviderWithBackchannel(
            workspace,
            backchannel =>
            {
                backchannel.ResourceSnapshots = [CreateSnapshot("myresource")];
                backchannel.TerminalInfoResponse = new GetTerminalInfoResponse
                {
                    IsAvailable = true,
                    Replicas = []
                };
            });
        using (provider)
        {
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("terminal myresource");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        }
    }

    [Fact]
    public async Task TerminalCommand_WhenReplicaIndexOutOfRange_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var (provider, _) = CreateProviderWithBackchannel(
            workspace,
            backchannel =>
            {
                backchannel.ResourceSnapshots = [CreateSnapshot("myresource")];
                backchannel.TerminalInfoResponse = new GetTerminalInfoResponse
                {
                    IsAvailable = true,
                    Replicas =
                    [
                        new TerminalReplicaInfo
                        {
                            ReplicaIndex = 0,
                            Label = "myresource-0",
                            ConsumerUdsPath = "/tmp/does-not-exist-0.sock",
                            IsAlive = true
                        },
                        new TerminalReplicaInfo
                        {
                            ReplicaIndex = 1,
                            Label = "myresource-1",
                            ConsumerUdsPath = "/tmp/does-not-exist-1.sock",
                            IsAlive = true
                        }
                    ]
                };
            });
        using (provider)
        {
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("terminal myresource --replica 99");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        }
    }

    [Fact]
    public async Task TerminalCommand_DisplayNameMatchesParentResource()
    {
        // Replicated resources share a DisplayName equal to the parent resource that
        // carries the TerminalAnnotation. Passing the parent name on the CLI must
        // resolve to the same canonical name when looking up terminal info.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        string? capturedResourceName = null;

        var (provider, backchannel) = CreateProviderWithBackchannel(
            workspace,
            bc =>
            {
                bc.ResourceSnapshots =
                [
                    CreateSnapshot("myresource-0", displayName: "myresource"),
                    CreateSnapshot("myresource-1", displayName: "myresource")
                ];
                bc.TerminalInfoResponse = new GetTerminalInfoResponse
                {
                    IsAvailable = false
                };
            });
        using (provider)
        {
            // Wrap the test backchannel's terminal info call to capture the canonical name.
            var monitor = (TestAuxiliaryBackchannelMonitor)provider.GetRequiredService<IAuxiliaryBackchannelMonitor>();
            var capturing = new CapturingTerminalAppHostBackchannel(backchannel, name => capturedResourceName = name);
            monitor.ClearConnections();
            monitor.AddConnection("hash1", "socket.hash1", capturing);

            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("terminal myresource");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            // IsAvailable=false → InvalidCommand, but we should see the canonical
            // parent name "myresource" passed to GetTerminalInfoAsync, not "myresource-0".
            Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
            Assert.Equal("myresource", capturedResourceName);
        }
    }

    [Fact]
    public async Task TerminalCommand_NonInteractiveMultiReplicaWithoutFlag_ReturnsInvalidCommand()
    {
        // When stdin or stdout is redirected and the resource has more than one replica,
        // the command must require --replica explicitly (rather than try to prompt).
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var (provider, _) = CreateProviderWithBackchannel(
            workspace,
            backchannel =>
            {
                backchannel.ResourceSnapshots = [CreateSnapshot("myresource")];
                backchannel.TerminalInfoResponse = new GetTerminalInfoResponse
                {
                    IsAvailable = true,
                    Replicas =
                    [
                        new TerminalReplicaInfo
                        {
                            ReplicaIndex = 0,
                            Label = "myresource-0",
                            ConsumerUdsPath = "/tmp/does-not-exist-0.sock",
                            IsAlive = true
                        },
                        new TerminalReplicaInfo
                        {
                            ReplicaIndex = 1,
                            Label = "myresource-1",
                            ConsumerUdsPath = "/tmp/does-not-exist-1.sock",
                            IsAlive = true
                        }
                    ]
                };
            });
        using (provider)
        {
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("terminal myresource");
            // Tests run with both stdout and stdin redirected (xUnit pipes them), so
            // Console.IsInputRedirected and Console.IsOutputRedirected are both true.
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        }
    }

    private (ServiceProvider Provider, TestAppHostAuxiliaryBackchannel Backchannel) CreateProviderWithBackchannel(
        TemporaryWorkspace workspace,
        Action<TestAppHostAuxiliaryBackchannel> configure)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 1234
            },
            SupportsTerminalsV1 = true
        };
        configure(backchannel);
        monitor.AddConnection("hash1", "socket.hash1", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });

        return (services.BuildServiceProvider(), backchannel);
    }

    private static ResourceSnapshot CreateSnapshot(string name, string? displayName = null)
    {
        return new ResourceSnapshot
        {
            Name = name,
            DisplayName = displayName,
            ResourceType = "Project",
            State = "Running"
        };
    }

    /// <summary>
    /// Wraps an inner backchannel and captures the resource name passed to
    /// <see cref="IAppHostAuxiliaryBackchannel.GetTerminalInfoAsync"/>. All other calls
    /// delegate to the inner instance.
    /// </summary>
    private sealed class CapturingTerminalAppHostBackchannel : IAppHostAuxiliaryBackchannel
    {
        private readonly TestAppHostAuxiliaryBackchannel _inner;
        private readonly Action<string> _onGetTerminalInfo;

        public CapturingTerminalAppHostBackchannel(TestAppHostAuxiliaryBackchannel inner, Action<string> onGetTerminalInfo)
        {
            _inner = inner;
            _onGetTerminalInfo = onGetTerminalInfo;
        }

        public string Hash => _inner.Hash;
        public string SocketPath => _inner.SocketPath;
        public AppHostInformation? AppHostInfo => _inner.AppHostInfo;
        public bool IsInScope => _inner.IsInScope;
        public DateTimeOffset ConnectedAt => _inner.ConnectedAt;
        public bool SupportsV2 => _inner.SupportsV2;
        public bool SupportsTerminalsV1 => _inner.SupportsTerminalsV1;

        public Task<GetTerminalInfoResponse> GetTerminalInfoAsync(string resourceName, CancellationToken cancellationToken = default)
        {
            _onGetTerminalInfo(resourceName);
            return _inner.GetTerminalInfoAsync(resourceName, cancellationToken);
        }

        public Task<global::Aspire.Cli.Backchannel.DashboardUrlsState?> GetDashboardUrlsAsync(CancellationToken cancellationToken = default)
            => _inner.GetDashboardUrlsAsync(cancellationToken);
        public Task<List<ResourceSnapshot>> GetResourceSnapshotsAsync(bool includeHidden, CancellationToken cancellationToken = default)
            => _inner.GetResourceSnapshotsAsync(includeHidden, cancellationToken);
        public IAsyncEnumerable<ResourceSnapshot> WatchResourceSnapshotsAsync(bool includeHidden, CancellationToken cancellationToken = default)
            => _inner.WatchResourceSnapshotsAsync(includeHidden, cancellationToken);
        public IAsyncEnumerable<ResourceLogLine> GetResourceLogsAsync(string? resourceName = null, bool follow = false, CancellationToken cancellationToken = default)
            => _inner.GetResourceLogsAsync(resourceName, follow, cancellationToken);
        public Task<bool> StopAppHostAsync(CancellationToken cancellationToken = default)
            => _inner.StopAppHostAsync(cancellationToken);
        public Task<ExecuteResourceCommandResponse> ExecuteResourceCommandAsync(string resourceName, string commandName, CancellationToken cancellationToken = default)
            => _inner.ExecuteResourceCommandAsync(resourceName, commandName, cancellationToken);
        public Task<WaitForResourceResponse> WaitForResourceAsync(string resourceName, string status, int timeoutSeconds, CancellationToken cancellationToken = default)
            => _inner.WaitForResourceAsync(resourceName, status, timeoutSeconds, cancellationToken);
        public Task<global::ModelContextProtocol.Protocol.CallToolResult> CallResourceMcpToolAsync(string resourceName, string toolName, IReadOnlyDictionary<string, global::System.Text.Json.JsonElement>? arguments, CancellationToken cancellationToken = default)
            => _inner.CallResourceMcpToolAsync(resourceName, toolName, arguments, cancellationToken);
        public Task<GetDashboardInfoResponse?> GetDashboardInfoV2Async(CancellationToken cancellationToken = default)
            => _inner.GetDashboardInfoV2Async(cancellationToken);

        public void Dispose() => _inner.Dispose();
    }
}
