// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
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
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, o => o.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled]);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal --help");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task TerminalCommand_WhenNoSubcommand_PrintsHelpAndFails()
    {
        // The 'terminal' parent command is non-runnable; it prints help when invoked
        // alone and returns InvalidCommand to mirror the DashboardCommand pattern.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, o => o.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled]);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task TerminalAttachCommand_WhenNoResourceArgument_FailsParsing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, o => o.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled]);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal attach");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task TerminalCommand_WhenNoAppHostRunning_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, o => o.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled]);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal attach myresource");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Mirrors the LogsCommand behavior: no running AppHost is informational, not an error.
        Assert.Equal(CliExitCodes.Success, exitCode);
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
            var result = command.Parse("terminal attach myresource");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode);
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
            var result = command.Parse("terminal attach does-not-exist");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
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
            var result = command.Parse("terminal attach myresource");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
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
            var result = command.Parse("terminal attach myresource");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
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
            var result = command.Parse("terminal attach myresource --replica 99");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
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
            var result = command.Parse("terminal attach myresource");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            // IsAvailable=false → InvalidCommand, but we should see the canonical
            // parent name "myresource" passed to GetTerminalInfoAsync, not "myresource-0".
            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
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
            var result = command.Parse("terminal attach myresource");
            // Tests run with both stdout and stdin redirected (xUnit pipes them), so
            // Console.IsInputRedirected and Console.IsOutputRedirected are both true.
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        }
    }

    [Fact]
    public async Task TerminalPsCommand_WhenNoAppHostRunning_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, o => o.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled]);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("terminal ps");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Mirrors TerminalAttachCommand: no running AppHost is informational, not an error.
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task TerminalPsCommand_WhenAppHostLacksTerminalsV1Capability_ReturnsAppHostIncompatible()
    {
        // Older AppHosts that pre-date the 'terminals.v1' capability return
        // SupportsTerminalsV1=false; the command must surface that explicitly rather
        // than misleadingly listing nothing.
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
            var result = command.Parse("terminal ps");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode);
        }
    }

    [Fact]
    public async Task TerminalPsCommand_WhenNoTerminalsRegistered_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var (provider, _) = CreateProviderWithBackchannel(
            workspace,
            backchannel =>
            {
                backchannel.ListTerminalsResponse = new ListTerminalsResponse
                {
                    Terminals = Array.Empty<TerminalSummary>()
                };
            });
        using (provider)
        {
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("terminal ps");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);
        }
    }

    [Fact]
    public async Task TerminalPsCommand_WhenTerminalsPresent_RendersTableSuccessfully()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var (provider, _) = CreateProviderWithBackchannel(
            workspace,
            backchannel =>
            {
                backchannel.ListTerminalsResponse = new ListTerminalsResponse
                {
                    Terminals =
                    [
                        new TerminalSummary
                        {
                            ResourceName = "myresource",
                            DisplayName = "myresource",
                            ConfiguredColumns = 120,
                            ConfiguredRows = 30,
                            IsHostReachable = true,
                            Replicas =
                            [
                                new TerminalReplicaInfo
                                {
                                    ReplicaIndex = 0,
                                    Label = "myresource-0",
                                    ConsumerUdsPath = "/tmp/r0.sock",
                                    IsAlive = true,
                                    CurrentColumns = 130,
                                    CurrentRows = 32,
                                    AttachedPeerCount = 2
                                }
                            ]
                        }
                    ]
                };
            });
        using (provider)
        {
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("terminal ps");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);
        }
    }

    [Fact]
    public async Task TerminalPsCommand_JsonFormat_WhenEmpty_EmitsEmptyArray()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // Capture stdout so we can verify the actual JSON contract — exit code
        // alone passes vacuously even if no JSON was written, the formatter
        // silently fell back to the text table, or the output isn't valid JSON.
        // DisableAnsi keeps Spectre.Console from injecting escape codes into
        // the captured stream.
        var capturedOutput = new TestOutputTextWriter(outputHelper);
        var (provider, _) = CreateProviderWithBackchannel(
            workspace,
            backchannel =>
            {
                backchannel.ListTerminalsResponse = new ListTerminalsResponse
                {
                    Terminals = Array.Empty<TerminalSummary>()
                };
            },
            options =>
            {
                options.OutputTextWriter = capturedOutput;
                options.DisableAnsi = true;
            });
        using (provider)
        {
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("terminal ps --format json");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);

            // EmitJson is the only producer of stdout in this code path, so the
            // captured output is the serialised JSON document. Trim trailing
            // newlines added by DisplayRawText / the writer.
            var stdout = string.Join("\n", capturedOutput.Logs).Trim();
            Assert.Equal("[]", stdout);

            // Belt-and-braces: prove it parses as valid JSON and round-trips
            // to an empty TerminalPsJsonEntry collection.
            var parsed = JsonSerializer.Deserialize(stdout, TerminalPsJsonContext.Default.ListTerminalPsJsonEntry);
            Assert.NotNull(parsed);
            Assert.Empty(parsed);
        }
    }

    [Fact]
    public async Task TerminalPsCommand_JsonFormat_Verbose_WhenPopulated_RoundTripsContract()
    {
        // Locks down the public --format json --verbose schema (TerminalPsJsonEntry /
        // TerminalPsJsonReplica / TerminalPsJsonPeer). Scripts piping `aspire terminal
        // ps --format json` to jq depend on field names + casing + verbose-only Peers
        // visibility staying stable; any rename, drop, or accidental verbosity
        // regression here breaks consumers silently.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var capturedOutput = new TestOutputTextWriter(outputHelper);

        // Two-resource shape covers the interesting matrix:
        //   - resource A: reachable host, two replicas, second replica has peers.
        //   - resource B: unreachable host (degraded), Replicas still populated
        //                 with AppHost-known shape (per #6 fix).
        var (provider, _) = CreateProviderWithBackchannel(
            workspace,
            backchannel =>
            {
                backchannel.ListTerminalsResponse = new ListTerminalsResponse
                {
                    Terminals =
                    [
                        new TerminalSummary
                        {
                            ResourceName = "frontend",
                            DisplayName = "Frontend",
                            ConfiguredColumns = 120,
                            ConfiguredRows = 30,
                            IsHostReachable = true,
                            Replicas =
                            [
                                new TerminalReplicaInfo
                                {
                                    ReplicaIndex = 0,
                                    Label = "frontend-0",
                                    ConsumerUdsPath = "/tmp/frontend-0.host.sock",
                                    IsAlive = true,
                                    ExitCode = null,
                                    ProducerConnected = true,
                                    RestartCount = 0,
                                    CurrentColumns = 130,
                                    CurrentRows = 32,
                                    AttachedPeerCount = 0,
                                    Peers = Array.Empty<TerminalPeerInfo>(),
                                },
                                new TerminalReplicaInfo
                                {
                                    ReplicaIndex = 1,
                                    Label = "frontend-1",
                                    ConsumerUdsPath = "/tmp/frontend-1.host.sock",
                                    IsAlive = true,
                                    ExitCode = null,
                                    ProducerConnected = true,
                                    RestartCount = 2,
                                    CurrentColumns = 200,
                                    CurrentRows = 50,
                                    AttachedPeerCount = 2,
                                    Peers =
                                    [
                                        new TerminalPeerInfo { PeerId = "peer-a", DisplayName = "aspire-cli:1234" },
                                        new TerminalPeerInfo { PeerId = "peer-b", DisplayName = null },
                                    ],
                                }
                            ]
                        },
                        new TerminalSummary
                        {
                            ResourceName = "backend",
                            DisplayName = "Backend",
                            ConfiguredColumns = 100,
                            ConfiguredRows = 24,
                            IsHostReachable = false,
                            Replicas =
                            [
                                new TerminalReplicaInfo
                                {
                                    ReplicaIndex = 0,
                                    Label = "backend-0",
                                    ConsumerUdsPath = "/tmp/backend-0.host.sock",
                                    IsAlive = false,
                                    ExitCode = null,
                                    ProducerConnected = false,
                                    RestartCount = 0,
                                    CurrentColumns = null,
                                    CurrentRows = null,
                                    AttachedPeerCount = null,
                                    Peers = null,
                                }
                            ]
                        }
                    ]
                };
            },
            options =>
            {
                options.OutputTextWriter = capturedOutput;
                options.DisableAnsi = true;
            });

        using (provider)
        {
            var command = provider.GetRequiredService<RootCommand>();
            var result = command.Parse("terminal ps --format json --verbose");
            var exitCode = await result.InvokeAsync().DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, exitCode);

            var stdout = string.Join("\n", capturedOutput.Logs).Trim();
            var entries = JsonSerializer.Deserialize(stdout, TerminalPsJsonContext.Default.ListTerminalPsJsonEntry);
            Assert.NotNull(entries);
            Assert.Equal(2, entries.Count);

            var frontend = entries[0];
            Assert.Equal("frontend", frontend.ResourceName);
            Assert.Equal("Frontend", frontend.DisplayName);
            Assert.Equal(120, frontend.ConfiguredColumns);
            Assert.Equal(30, frontend.ConfiguredRows);
            Assert.True(frontend.IsHostReachable);
            Assert.Equal(2, frontend.Replicas.Length);

            var replica0 = frontend.Replicas[0];
            Assert.Equal(0, replica0.ReplicaIndex);
            Assert.True(replica0.IsAlive);
            Assert.True(replica0.ProducerConnected);
            Assert.Equal(0, replica0.RestartCount);
            Assert.Equal(130, replica0.CurrentColumns);
            Assert.Equal(32, replica0.CurrentRows);
            Assert.Equal(0, replica0.AttachedPeerCount);
            // Peers should be present (empty array) under --verbose. We accept
            // either null or empty here because the JSON DTO omits empty arrays
            // via JsonIgnoreCondition.WhenWritingNull only for null — empty
            // arrays still serialise as `[]`.
            Assert.NotNull(replica0.Peers);
            Assert.Empty(replica0.Peers);

            var replica1 = frontend.Replicas[1];
            Assert.Equal(1, replica1.ReplicaIndex);
            Assert.Equal(2, replica1.RestartCount);
            Assert.Equal(200, replica1.CurrentColumns);
            Assert.Equal(50, replica1.CurrentRows);
            Assert.Equal(2, replica1.AttachedPeerCount);
            Assert.NotNull(replica1.Peers);
            Assert.Equal(2, replica1.Peers.Length);
            Assert.Equal("peer-a", replica1.Peers[0].PeerId);
            Assert.Equal("aspire-cli:1234", replica1.Peers[0].DisplayName);
            Assert.Equal("peer-b", replica1.Peers[1].PeerId);
            Assert.Null(replica1.Peers[1].DisplayName);

            var backend = entries[1];
            Assert.Equal("backend", backend.ResourceName);
            Assert.False(backend.IsHostReachable);
            // Per #6 fix: degraded shape still surfaces Replicas so operators
            // can diagnose which replicas the AppHost expected.
            Assert.Single(backend.Replicas);
            Assert.False(backend.Replicas[0].IsAlive);
            Assert.Null(backend.Replicas[0].CurrentColumns);
            Assert.Null(backend.Replicas[0].AttachedPeerCount);

            // Verify camelCase field names are present in the raw output
            // (JsonNamingPolicy.CamelCase is part of the contract — a script
            // doing `jq '.[0].resourceName'` must keep working).
            Assert.Contains("\"resourceName\"", stdout);
            Assert.Contains("\"isHostReachable\"", stdout);
            Assert.Contains("\"replicas\"", stdout);
            Assert.Contains("\"peers\"", stdout);
        }
    }

    private (ServiceProvider Provider, TestAppHostAuxiliaryBackchannel Backchannel) CreateProviderWithBackchannel(
        TemporaryWorkspace workspace,
        Action<TestAppHostAuxiliaryBackchannel> configure,
        Action<CliServiceCollectionTestOptions>? configureOptions = null)
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
            options.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled];
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            configureOptions?.Invoke(options);
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
        public bool SupportsV3 => _inner.SupportsV3;
        public bool SupportsTerminalsV1 => _inner.SupportsTerminalsV1;

        public Task<GetTerminalInfoResponse> GetTerminalInfoAsync(string resourceName, CancellationToken cancellationToken = default)
        {
            _onGetTerminalInfo(resourceName);
            return _inner.GetTerminalInfoAsync(resourceName, cancellationToken);
        }

        public Task<ListTerminalsResponse> ListTerminalsAsync(CancellationToken cancellationToken = default)
        {
            return _inner.ListTerminalsAsync(cancellationToken);
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
        public Task<ExecuteResourceCommandResponse> ExecuteResourceCommandAsync(string resourceName, string commandName, ExecuteResourceCommandOptions? options = null, CancellationToken cancellationToken = default)
            => _inner.ExecuteResourceCommandAsync(resourceName, commandName, options, cancellationToken);
        public Task<WaitForResourceResponse> WaitForResourceAsync(string resourceName, string status, int timeoutSeconds, CancellationToken cancellationToken = default)
            => _inner.WaitForResourceAsync(resourceName, status, timeoutSeconds, cancellationToken);
        public Task<global::ModelContextProtocol.Protocol.CallToolResult> CallResourceMcpToolAsync(string resourceName, string toolName, IReadOnlyDictionary<string, global::System.Text.Json.JsonElement>? arguments, CancellationToken cancellationToken = default)
            => _inner.CallResourceMcpToolAsync(resourceName, toolName, arguments, cancellationToken);
        public Task<GetDashboardInfoResponse?> GetDashboardInfoV2Async(CancellationToken cancellationToken = default)
            => _inner.GetDashboardInfoV2Async(cancellationToken);

        public Task<GetAppHostInfoResponse?> GetAppHostInfoV2Async(CancellationToken cancellationToken = default)
            => _inner.GetAppHostInfoV2Async(cancellationToken);

        public Task<WaitForAppHostReadyResponse?> WaitForAppHostReadyAsync(CancellationToken cancellationToken = default)
            => _inner.WaitForAppHostReadyAsync(cancellationToken);

        public IAsyncEnumerable<ResourceLogLine> GetConsoleLogsAsync(GetConsoleLogsRequest request, CancellationToken cancellationToken = default)
            => _inner.GetConsoleLogsAsync(request, cancellationToken);

        public IAsyncEnumerable<ResourceLogBatch> GetConsoleLogBatchesAsync(GetConsoleLogsRequest request, CancellationToken cancellationToken = default)
            => _inner.GetConsoleLogBatchesAsync(request, cancellationToken);

        public void Dispose() => _inner.Dispose();
    }
}
