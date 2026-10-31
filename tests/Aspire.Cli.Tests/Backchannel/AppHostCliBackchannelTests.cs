// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Backchannel;

public class AppHostCliBackchannelTests
{
    [Fact]
    public async Task UploadFileAsync_ExceedsConfiguredLimit_ThrowsWithConfigHint()
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [KnownConfigNames.MaxFileUploadSize] = "1024" // 1 KB limit
        });
        var telemetry = TestTelemetryHelper.CreateInitializedTelemetry();
        using var profilingTelemetry = new ProfilingTelemetry(new ConfigurationBuilder().Build());
        var backchannel = new AppHostCliBackchannel(
            NullLogger<AppHostCliBackchannel>.Instance,
            environment,
            telemetry,
            profilingTelemetry);

        var tempDir = Directory.CreateTempSubdirectory("aspire-upload-test-");
        try
        {
            var tempFile = Path.Combine(tempDir.FullName, "large.bin");
            await File.WriteAllBytesAsync(tempFile, new byte[2048]); // 2 KB exceeds the 1 KB limit

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => backchannel.UploadFileAsync(tempFile, "large.bin", 1, "File", CancellationToken.None));

            Assert.Contains("large.bin", ex.Message);
            Assert.Contains("2048", ex.Message);
            Assert.Contains("1024", ex.Message);
            Assert.Contains(KnownConfigNames.MaxFileUploadSize, ex.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RequestStopAsync_AppHostClosesBackchannelBeforeAcknowledging_Completes()
    {
        // Reproduces the AppHost shutdown race behind `aspire do --list-steps` failing with exit code 6:
        // the AppHost calls StopApplication() inside RequestStopAsync, and host shutdown closes the
        // backchannel before StreamJsonRpc writes the response.
        var cancellationToken = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("aspire-bch-");
        try
        {
            var socketPath = Path.Combine(tempDir.FullName, "apphost.sock");
            using var server = new ClosingOnStopAppHostServer(socketPath);

            var telemetry = TestTelemetryHelper.CreateInitializedTelemetry();
            using var profilingTelemetry = new ProfilingTelemetry(new ConfigurationBuilder().Build());
            var backchannel = new AppHostCliBackchannel(
                NullLogger<AppHostCliBackchannel>.Instance,
                new TestEnvironment(),
                telemetry,
                profilingTelemetry);

            var acceptTask = server.AcceptAsync(cancellationToken);
            await backchannel.ConnectAsync(socketPath, retryCount: 0, cancellationToken);
            await acceptTask;

            await backchannel.RequestStopAsync(CancellationToken.None).WaitAsync(cancellationToken);

            Assert.True(server.StopRequested.Task.IsCompletedSuccessfully);
            await backchannel.WaitForDisconnectAsync(cancellationToken);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RequestStopAsync_CallerCancelsWhileBackchannelIsOpen_Throws()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("aspire-bch-");
        try
        {
            var socketPath = Path.Combine(tempDir.FullName, "apphost.sock");
            using var server = new ClosingOnStopAppHostServer(socketPath, closeOnStop: false);

            var telemetry = TestTelemetryHelper.CreateInitializedTelemetry();
            using var profilingTelemetry = new ProfilingTelemetry(new ConfigurationBuilder().Build());
            var backchannel = new AppHostCliBackchannel(
                NullLogger<AppHostCliBackchannel>.Instance,
                new TestEnvironment(),
                telemetry,
                profilingTelemetry);

            var acceptTask = server.AcceptAsync(cancellationToken);
            await backchannel.ConnectAsync(socketPath, retryCount: 0, cancellationToken);
            await acceptTask;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var stopTask = backchannel.RequestStopAsync(cts.Token);
            await server.StopRequested.Task.WaitAsync(cancellationToken);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopTask);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private sealed class ClosingOnStopAppHostServer : IDisposable
    {
        private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly bool _closeOnStop;
        private JsonRpc? _rpc;

        public ClosingOnStopAppHostServer(string socketPath, bool closeOnStop = true)
        {
            _closeOnStop = closeOnStop;
            _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            _listener.Listen(1);
        }

        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AcceptAsync(CancellationToken cancellationToken)
        {
            var socket = await _listener.AcceptAsync(cancellationToken);
            var stream = new NetworkStream(socket, ownsSocket: true);
            var handler = new HeaderDelimitedMessageHandler(stream, stream, BackchannelJsonSerializerContext.CreateRpcMessageFormatter());
            _rpc = new JsonRpc(handler, new RpcTarget(this));
            _rpc.StartListening();
        }

        public void Dispose()
        {
            _rpc?.Dispose();
            _listener.Dispose();
        }

        private sealed class RpcTarget(ClosingOnStopAppHostServer server)
        {
            private readonly string[] _capabilities = ["baseline.v2"];

            public Task<string[]> GetCapabilitiesAsync() => Task.FromResult(_capabilities);

            public Task RequestStopAsync(CancellationToken cancellationToken)
            {
                server.StopRequested.TrySetResult();

                if (server._closeOnStop)
                {
                    // Like AppHost shutdown, close the connection before this call's response is written.
                    server._rpc!.Dispose();
                }

                return Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }

    [Fact]
    public async Task UploadFileAsync_FileBelowConfiguredLimit_PassesSizeCheck()
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [KnownConfigNames.MaxFileUploadSize] = "4096" // 4 KB limit
        });
        var telemetry = TestTelemetryHelper.CreateInitializedTelemetry();
        using var profilingTelemetry = new ProfilingTelemetry(new ConfigurationBuilder().Build());
        var backchannel = new AppHostCliBackchannel(
            NullLogger<AppHostCliBackchannel>.Instance,
            environment,
            telemetry,
            profilingTelemetry);

        var tempDir = Directory.CreateTempSubdirectory("aspire-upload-test-");
        try
        {
            var tempFile = Path.Combine(tempDir.FullName, "small.bin");
            await File.WriteAllBytesAsync(tempFile, new byte[1024]); // 1 KB, below the 4 KB limit

            // The file is within the limit, so it should pass the size check and fail later
            // when trying to get the RPC connection (no connection available). A cancellation
            // exception proves the size check passed.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => backchannel.UploadFileAsync(tempFile, "small.bin", 1, "File", cts.Token));

            // If we get a cancellation exception (waiting for RPC), the size check passed.
            Assert.DoesNotContain("exceeds the maximum upload size", ex.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
