// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // IFileSystemService is for evaluation purposes only.

using System.Globalization;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Aspire.Hosting.Tests.Dcp;

public class KubernetesServiceTests
{
    // Verifies that establishing the connection happens inside the retry loop: when the kubeconfig does not
    // exist yet (DCP has not finished writing it), the operation waits and succeeds once it appears.
    [Fact]
    public async Task ExecuteWithRetry_EstablishesConnection_WhenKubeconfigInitiallyMissing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        // No kubeconfig on disk initially.
        Assert.False(File.Exists(kubeconfigPath));

        var listTask = service.ListAsync<Container>(cancellationToken: cts.Token);

        await Task.Delay(300, cts.Token);

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteWithRetry_UsesInitializationTimeout_WhenKubeconfigAppearsAfterApiRetryBudget()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromSeconds(1));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        var listTask = service.ListAsync<Container>(cancellationToken: cts.Token);

        // The kubeconfig appears after the normal API retry budget. Initial DCP connection establishment
        // needs its own startup budget because DCP has not exposed an API endpoint yet.
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteWithRetry_UsesInitializationTimeout_UntilFirstApiOperationSucceeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationTimeout: TimeSpan.FromSeconds(5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        // The fourth API request succeeds after exponential retry delays of 100, 200, and 400 milliseconds.
        // It is reachable under the initialization budget but not the 500-millisecond steady-state budget.
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, successfulRequestNumber: 4);
        var listTask = service.ListAsync<Container>(cancellationToken: cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteWithRetry_UsesApiRetryDuration_AfterFirstApiOperationSucceeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationTimeout: TimeSpan.FromSeconds(5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await service.ListAsync<Container>(cancellationToken: cts.Token);
        Assert.Empty(result);

        server.FailNextRequests(3);
        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => service.ListAsync<Container>(cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ExecuteWithRetry_CancelsApiRequest_WhenRetryDurationExpires()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationTimeout: TimeSpan.FromSeconds(5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);
        Assert.Empty(await service.ListAsync<Container>(cancellationToken: cts.Token));

        server.DelayResponses(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => service.ListAsync<Container>(cancellationToken: cts.Token));
        await server.WaitForRequestCancellationAsync(cts.Token);
    }

    [Fact]
    public async Task WatchAsync_DoesNotMarkApiReady_WhileHttpResponseIsPending()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationTimeout: TimeSpan.FromSeconds(5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        server.BlockWatchResponses();
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var watchEnumerator = service.WatchAsync<Container>(cancellationToken: watchCts.Token).GetAsyncEnumerator();
        var watchTask = watchEnumerator.MoveNextAsync().AsTask();
        await server.WaitForWatchRequestAsync(cts.Token);

        // Three conflict retries take longer than the steady-state budget but remain within the initialization budget.
        server.FailNextRequests(3);
        try
        {
            Assert.Empty(await service.ListAsync<Container>(cancellationToken: cts.Token));
        }
        finally
        {
            watchCts.Cancel();
            await server.WaitForRequestCancellationAsync(cts.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchTask);
        }
    }

    // Verifies that establishing the connection survives a partially-written kubeconfig: when the file exists
    // but DCP has only flushed part of it (so it does not yet parse as a valid kubeconfig), the read is retried
    // and the operation succeeds once the complete, valid kubeconfig is written.
    [Fact]
    public async Task ExecuteWithRetry_EstablishesConnection_WhenKubeconfigInitiallyPartiallyWritten()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        // Simulate DCP having flushed only the first part of the kubeconfig
        WritePartialKubeconfig(kubeconfigPath);

        var listTask = service.ListAsync<Container>(cancellationToken: cts.Token);

        // Give the read pipeline time to observe and retry the partial file before we finish writing it.
        await Task.Delay(300, cts.Token);

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);

        // Finish the write by appending the remainder onto the same file. In-flight DCP calls will now succeed.
        CompleteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    private static (KubernetesService Service, string KubeconfigPath, IDisposable FileSystem) CreateService(
        TimeSpan? maxRetryDuration = null,
        TimeSpan? kubernetesInitializationTimeout = null)
    {
        var configuration = new ConfigurationBuilder().Build();

        // Decouple the kubeconfig location from the production FileSystemService
        var fileSystem = new TestFileSystemService();
        try
        {
            var locations = new Locations(fileSystem);

            var dcpOptions = Options.Create(new DcpOptions
            {
                // Poll quickly so the kubeconfig file-wait/read retries react promptly in tests.
                KubernetesConfigReadRetryIntervalMilliseconds = 50,
                KubernetesConfigReadRetryCount = 300,
            });

            var service = new KubernetesService(NullLogger<KubernetesService>.Instance, dcpOptions, locations, configuration)
            {
                // Generous enough that the test can flip the kubeconfig before the retry budget is exhausted.
                MaxRetryDuration = maxRetryDuration ?? TimeSpan.FromSeconds(30),
                KubernetesInitializationTimeout = kubernetesInitializationTimeout ?? TimeSpan.FromSeconds(60),
            };

            return (service, locations.DcpKubeconfigPath, fileSystem);
        }
        catch
        {
            // Don't orphan the temp directory if wiring up the service fails before the test takes ownership.
            fileSystem.Dispose();
            throw;
        }
    }

    private static void WriteKubeconfig(string path, int port)
    {
        // Minimal kubeconfig pointing at a plain-HTTP loopback endpoint with no auth, which is all the
        // DcpKubernetesClient needs to issue custom-object requests against the fake server.
        var content = string.Format(CultureInfo.InvariantCulture, """
            apiVersion: v1
            kind: Config
            clusters:
            - name: dcp
              cluster:
                server: http://127.0.0.1:{0}
            contexts:
            - name: dcp
              context:
                cluster: dcp
                user: dcp
            current-context: dcp
            users:
            - name: dcp
              user:
                token: dcp-test-token
            """, port);

        // Write atomically (temp file + move on the same volume) so a concurrent read by the service never
        // observes a half-written file.
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void WritePartialKubeconfig(string path)
    {
        // A genuine prefix of the final kubeconfig that stops in the middle of the double-quoted server value.
        // The unterminated quote makes this deterministically fail YAML parsing, which models DCP having only
        // flushed part of the file. There is intentionally no trailing newline, so appending the remainder in
        // CompleteKubeconfig closes the quote and yields exactly-valid YAML.
        File.WriteAllText(path, """
            apiVersion: v1
            kind: Config
            clusters:
            - name: dcp
              cluster:
                server: "http://127.0.0.1:
            """);
    }

    private static void CompleteKubeconfig(string path, int port)
    {
        // Append (do not rewrite) the remainder of the kubeconfig that WritePartialKubeconfig left unfinished.
        // The remainder begins by closing the open server scalar ({port}") so the combined file parses as a
        // valid kubeconfig pointing at the loopback fake server with no auth.
        File.AppendAllText(path, string.Format(CultureInfo.InvariantCulture, """
            {0}"
            contexts:
            - name: dcp
              context:
                cluster: dcp
                user: dcp
            current-context: dcp
            users:
            - name: dcp
              user:
                token: dcp-test-token
            """, port));
    }

    // A self-contained IFileSystemService for these tests. It hands Locations a single, uniquely-suffixed temp
    // directory that the test owns, decoupling the kubeconfig location from the production FileSystemService so
    // concurrent runs on the same machine never share a path. Every file a test writes lives under this root, so
    // disposing the fake (always, via `using`) removes the kubeconfig and any partial/temp files regardless of the
    // test outcome.
    private sealed class TestFileSystemService : IFileSystemService, IDisposable
    {
        private readonly TestTempFileSystemService _tempDirectory = new();

        public ITempFileSystemService TempDirectory => _tempDirectory;

        public void Dispose() => _tempDirectory.Dispose();

        private sealed class TestTempFileSystemService : ITempFileSystemService, IDisposable
        {
            private string? _root;

            public TempDirectory CreateTempSubdirectory(string? prefix = null)
            {
                // Created lazily (Locations calls this exactly once) so the directory can't be orphaned if the
                // test fails to take ownership, and with a random suffix so each test instance is isolated.
                _root ??= Directory.CreateTempSubdirectory("test-kubeconfig-").FullName;
                return new TestTempDirectory(_root);
            }

            public TempFile CreateTempFile(string? fileName = null)
                => throw new NotSupportedException("The kubeconfig tests only allocate a temp subdirectory.");

            public void Dispose()
            {
                if (_root is null)
                {
                    return;
                }

                try
                {
                    if (Directory.Exists(_root))
                    {
                        Directory.Delete(_root, recursive: true);
                    }
                }
                catch
                {
                    // Best-effort cleanup; a teardown failure must never mask the test result.
                }
            }
        }

        // The owning TestTempFileSystemService deletes the root recursively on Dispose, 
        // so this handle has nothing of its own to release.
        private sealed class TestTempDirectory(string path) : TempDirectory
        {
            public override string Path => path;

            public override void Dispose()
            {
            }
        }
    }

    // A minimal stand-in for the DCP API server. It can return conflicts for a configured number of requests,
    // then answers with an empty Kubernetes list that ListAsync<Container>() can deserialize successfully.
    //
    // It runs a real Kestrel server bound to port 0 so the OS assigns a free port that Kestrel actually binds and
    // holds for the lifetime of the server. The bound port is read back after startup. This avoids the classic
    // "probe a free port then release it and hope nobody grabs it before we rebind" race.
    private sealed class TestDcpApiServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ResponseState _responseState;

        private TestDcpApiServer(WebApplication app, int port, ResponseState responseState)
        {
            _app = app;
            _responseState = responseState;
            Port = port;
        }

        public int Port { get; }

        public void FailNextRequests(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            Volatile.Write(
                ref _responseState.SuccessfulRequestNumber,
                Volatile.Read(ref _responseState.RequestCount) + count + 1);
        }

        public void DelayResponses(TimeSpan delay)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
            Interlocked.Exchange(ref _responseState.ResponseDelayTicks, delay.Ticks);
        }

        public void BlockWatchResponses()
        {
            Volatile.Write(ref _responseState.BlockWatchResponses, true);
        }

        public Task WaitForWatchRequestAsync(CancellationToken cancellationToken)
        {
            return _responseState.WatchRequestArrived.Task.WaitAsync(cancellationToken);
        }

        public Task WaitForRequestCancellationAsync(CancellationToken cancellationToken)
        {
            return _responseState.RequestCancellationObserved.Task.WaitAsync(cancellationToken);
        }

        public static async Task<TestDcpApiServer> StartAsync(
            CancellationToken cancellationToken = default,
            int successfulRequestNumber = 1)
        {
            var builder = WebApplication.CreateSlimBuilder();
            // Keep the test output clean; the fake server's logs are noise.
            builder.Logging.ClearProviders();
            // Port 0 lets the OS pick a free port that Kestrel binds and holds. After StartAsync the addresses
            // feature (exposed via app.Urls) is rewritten with the resolved address, so we can read the real port.
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();
            var responseState = new ResponseState
            {
                SuccessfulRequestNumber = successfulRequestNumber,
            };

            app.Run(async context =>
            {
                var isWatchRequest = context.Request.Query.TryGetValue("watch", out var watchValues)
                    && string.Equals(watchValues.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                if (isWatchRequest && Volatile.Read(ref responseState.BlockWatchResponses))
                {
                    responseState.WatchRequestArrived.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        responseState.RequestCancellationObserved.TrySetResult(true);
                        throw;
                    }
                }

                var responseDelayTicks = Interlocked.Read(ref responseState.ResponseDelayTicks);
                if (responseDelayTicks > 0)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromTicks(responseDelayTicks), context.RequestAborted);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        responseState.RequestCancellationObserved.TrySetResult(true);
                        throw;
                    }
                }

                if (Interlocked.Increment(ref responseState.RequestCount) < Volatile.Read(ref responseState.SuccessfulRequestNumber))
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"apiVersion":"usvc-dev.developer.microsoft.com/v1","kind":"ContainerList","items":[]}""");
            });

            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            // e.g. "http://127.0.0.1:54321" -> 54321
            var address = app.Urls.First();
            var port = new Uri(address).Port;

            return new TestDcpApiServer(app, port, responseState);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class ResponseState
        {
            public bool BlockWatchResponses;
            public int RequestCount;
            public TaskCompletionSource<bool> RequestCancellationObserved { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public long ResponseDelayTicks;
            public int SuccessfulRequestNumber;
            public TaskCompletionSource<bool> WatchRequestArrived { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
