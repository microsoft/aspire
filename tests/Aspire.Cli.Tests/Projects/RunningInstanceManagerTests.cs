// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Projects;

public class RunningInstanceManagerTests
{
    [Fact]
    public async Task StopRunningInstanceAsync_DeletesSocketFile_WhenStopSucceeds()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/17587.
        //
        // After a successful stop, the CLI must delete the auxiliary backchannel socket file. If the
        // file is left behind, a later command (e.g. 'aspire add' or 'aspire stop') rediscovers it via
        // FindMatchingNonOrphanedSockets and tries to connect to a now-defunct process, failing with
        // "Unable to stop one or more running Aspire AppHost instances". This is most visible on Windows
        // where the dead AppHost's PID can be reused (so the orphan-pruning heuristic believes the
        // process is still alive), which is why a unit test is the deterministic, cross-platform guard.

        // The AppHost process reported by the fake backchannel must be one MonitorProcessesForTerminationAsync
        // observes as already terminated so StopRunningInstanceAsync reaches the socket-cleanup branch. A fabricated,
        // guaranteed-nonexistent PID makes Process.GetProcessById throw ArgumentException, which the monitor treats as
        // "terminated". We deliberately do NOT start a real process and reuse its exited PID: the OS can recycle that
        // exact PID for an unrelated live process before the monitor checks, which would make the monitor loop for the
        // full timeout, return stopped == false, and flake the cleanup assertion.
        const int exitedProcessId = int.MaxValue - 9;

        var socketDirectory = Directory.CreateTempSubdirectory("aspire-rim-");
        try
        {
            // Keep the socket path short: a UnixDomainSocketEndPoint is limited to ~104 bytes on macOS.
            var socketPath = Path.Combine(socketDirectory.FullName, "a.sock");
            using var server = TestAuxiliaryBackchannelUdsServer.Start(socketPath, exitedProcessId);

            // Binding the server creates the socket file on disk.
            Assert.True(File.Exists(socketPath));

            var manager = new RunningInstanceManager(NullLogger.Instance, new TestInteractionService(), TimeProvider.System, new ProfilingTelemetry(new ConfigurationBuilder().Build()));

            var stopped = await manager.StopRunningInstanceAsync(socketPath, CancellationToken.None).DefaultTimeout();

            Assert.True(stopped);
            Assert.True(server.Target.StopRequested);
            // The fix: the socket file must be removed once the instance has been stopped.
            Assert.False(File.Exists(socketPath));
        }
        finally
        {
            socketDirectory.Delete(recursive: true);
        }
    }

    private sealed class TestAuxiliaryBackchannelUdsServer : IDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<IDisposable> _disposables = [];

        private TestAuxiliaryBackchannelUdsServer(string socketPath, int appHostProcessId)
        {
            Target = new StoppableAppHostRpcTarget(appHostProcessId);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            _listener.Listen(1);
        }

        public StoppableAppHostRpcTarget Target { get; }

        public static TestAuxiliaryBackchannelUdsServer Start(string socketPath, int appHostProcessId)
        {
            var server = new TestAuxiliaryBackchannelUdsServer(socketPath, appHostProcessId);
            _ = server.AcceptConnectionAsync();
            return server;
        }

        private async Task AcceptConnectionAsync()
        {
            try
            {
                var serverSocket = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
                var serverStream = new NetworkStream(serverSocket, ownsSocket: true);
                var messageHandler = new HeaderDelimitedMessageHandler(serverStream, serverStream, BackchannelJsonSerializerContext.CreateRpcMessageFormatter());
                var rpc = new JsonRpc(messageHandler, Target);
                rpc.StartListening();
                _disposables.Add(rpc);
                _disposables.Add(messageHandler);
                _disposables.Add(serverStream);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Expected when the server is disposed before/while a connection is accepted.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }

            _listener.Dispose();
            _cts.Dispose();
        }
    }

    private sealed class StoppableAppHostRpcTarget
    {
        private readonly int _processId;
        private readonly string[] _capabilities =
        [
            AuxiliaryBackchannelCapabilities.V1,
            AuxiliaryBackchannelCapabilities.V2
        ];

        public StoppableAppHostRpcTarget(int processId)
        {
            _processId = processId;
        }

        public bool StopRequested { get; private set; }

        public Task<AppHostInformation> GetAppHostInformationAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;

            return Task.FromResult(new AppHostInformation
            {
                AppHostPath = "/path/to/AppHost.csproj",
                ProcessId = _processId
            });
        }

        public Task<GetCapabilitiesResponse> GetCapabilitiesAsync(GetCapabilitiesRequest? request = null, CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = cancellationToken;

            return Task.FromResult(new GetCapabilitiesResponse
            {
                Capabilities = _capabilities
            });
        }

        public Task StopAppHostAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            StopRequested = true;
            return Task.CompletedTask;
        }
    }
}
