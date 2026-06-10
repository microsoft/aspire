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

    private static (KubernetesService Service, string KubeconfigPath, IDisposable FileSystem) CreateService()
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
                MaxRetryDuration = TimeSpan.FromSeconds(30),
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

    // A minimal stand-in for the DCP API server. It answers every request with an empty Kubernetes list, which
    // is enough for ListAsync<Container>() to deserialize successfully.
    //
    // It runs a real Kestrel server bound to port 0 so the OS assigns a free port that Kestrel actually binds and
    // holds for the lifetime of the server. The bound port is read back after startup. This avoids the classic
    // "probe a free port then release it and hope nobody grabs it before we rebind" race.
    private sealed class TestDcpApiServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TestDcpApiServer(WebApplication app, int port)
        {
            _app = app;
            Port = port;
        }

        public int Port { get; }

        public static async Task<TestDcpApiServer> StartAsync(CancellationToken cancellationToken = default)
        {
            var builder = WebApplication.CreateSlimBuilder();
            // Keep the test output clean; the fake server's logs are noise.
            builder.Logging.ClearProviders();
            // Port 0 lets the OS pick a free port that Kestrel binds and holds. After StartAsync the addresses
            // feature (exposed via app.Urls) is rewritten with the resolved address, so we can read the real port.
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();

            // Answer every request with an empty container list.
            app.Run(async context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"apiVersion":"usvc-dev.developer.microsoft.com/v1","kind":"ContainerList","items":[]}""");
            });

            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            // e.g. "http://127.0.0.1:54321" -> 54321
            var address = app.Urls.First();
            var port = new Uri(address).Port;

            return new TestDcpApiServer(app, port);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
