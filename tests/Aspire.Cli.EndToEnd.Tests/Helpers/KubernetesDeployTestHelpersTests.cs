// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

public class KubernetesDeployTestHelpersTests
{
    [Fact]
    public void GetDeletePodCommand_WaitsForOldPodRemoval()
    {
        Assert.Equal(
            "kubectl delete pod app-statefulset-0 -n test-ns --wait=true",
            KubernetesDeployTestHelpers.GetDeletePodCommand("test-ns", "app-statefulset-0"));
    }

    [Fact]
    public void GetWaitForRecreatedPodReadyCommand_WaitsForCreationBeforeReadiness()
    {
        // Waiting for Ready on a StatefulSet pod that the controller has not recreated yet
        // fails immediately with NotFound, so readiness must be gated on creation.
        Assert.Equal(
            "kubectl wait --for=create pod/app-statefulset-0 -n test-ns --timeout=180s && " +
            "kubectl wait --for=condition=Ready pod/app-statefulset-0 -n test-ns --timeout=180s",
            KubernetesDeployTestHelpers.GetWaitForRecreatedPodReadyCommand("test-ns", "app-statefulset-0"));
    }

    [Fact]
    public async Task GetRetryingDownloadCommand_RetriesWhenServerReturnsError()
    {
        // A GitHub release download returning 504 must be retried, not saved as the file.
        await using var server = FakeDownloadServer.Start(static (attempt, stream, ct) => attempt == 1
            ? stream.WriteResponseAsync("504 Gateway Timeout", "gateway timeout", ct)
            : stream.WriteResponseAsync("200 OK", "payload", ct));

        var (exitCode, content) = await RunDownloadAsync(server.Url);

        Assert.Equal(0, exitCode);
        Assert.Equal("payload", content);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task GetRetryingDownloadCommand_RetriesWhenDownloadStalls()
    {
        // A connection that never responds must end the attempt so the next one can run
        // before the caller's 90s prompt timeout expires.
        await using var server = FakeDownloadServer.Start(static async (attempt, stream, ct) =>
        {
            if (attempt == 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            await stream.WriteResponseAsync("200 OK", "payload", ct);
        });

        var stopwatch = Stopwatch.StartNew();
        var (exitCode, content) = await RunDownloadAsync(server.Url);

        Assert.Equal(0, exitCode);
        Assert.Equal("payload", content);
        Assert.Equal(2, server.RequestCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(90), $"Download took {stopwatch.Elapsed}.");
    }

    private static async Task<(int ExitCode, string? Content)> RunDownloadAsync(string url)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The download command runs in a Linux shell.");

        var directory = Directory.CreateTempSubdirectory("aspire-download-test-");
        var destination = Path.Combine(directory.FullName, "download");
        var command = KubernetesDeployTestHelpers.GetRetryingDownloadCommand(
            "test", url, $"'{destination}'", $"test -s '{destination}'");

        var startInfo = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Fail rather than hang when an attempt is not bounded (the original stalled-download behavior).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The download command did not finish.");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(await stdout + await stderr);
        try
        {
            return (process.ExitCode, File.Exists(destination) ? await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken) : null);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FakeDownloadServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<int, NetworkStream, CancellationToken, Task> _respond;
        private readonly List<Task> _connections = [];
        private Task _acceptLoop = Task.CompletedTask;
        private int _requestCount;

        private FakeDownloadServer(Func<int, NetworkStream, CancellationToken, Task> respond) => _respond = respond;

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/file";

        public int RequestCount => Volatile.Read(ref _requestCount);

        public static FakeDownloadServer Start(Func<int, NetworkStream, CancellationToken, Task> respond)
        {
            var server = new FakeDownloadServer(respond);
            server._listener.Start();
            server._acceptLoop = server.AcceptAsync();
            return server;
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _connections.Add(HandleAsync(client));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var request = new StringBuilder();
                    while (!request.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await stream.ReadAsync(buffer, _cts.Token);
                        if (read == 0)
                        {
                            return;
                        }

                        request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    await _respond(Interlocked.Increment(ref _requestCount), stream, _cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _acceptLoop;
            await Task.WhenAll(_connections);
            _cts.Dispose();
        }
    }
}

internal static class FakeDownloadServerStreamExtensions
{
    public static Task WriteResponseAsync(this NetworkStream stream, string status, string body, CancellationToken cancellationToken)
    {
        var response = $"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).AsTask();
    }
}
