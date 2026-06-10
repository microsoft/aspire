// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // IFileSystemService is for evaluation purposes only.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.Configuration;
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

        using var server = new TestDcpApiServer();
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    private static (KubernetesService Service, string KubeconfigPath, IDisposable FileSystem) CreateService()
    {
        var configuration = new ConfigurationBuilder().Build();
        var fileSystem = new FileSystemService(configuration);
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

        // Computing the path also creates the (real) temp session directory the kubeconfig lives in.
        return (service, locations.DcpKubeconfigPath, fileSystem);
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

    private static int GetFreePort()
    {
        // Bind to port 0 to let the OS pick a free port, then release it. There is a small race before the
        // port is (intentionally, for the dead-port case) left unused, which is acceptable for a loopback test.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    // A minimal stand-in for the DCP API server. It answers every request with an empty Kubernetes list, which
    // is enough for ListAsync<Container>() to deserialize successfully.
    private sealed class TestDcpApiServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public TestDcpApiServer()
        {
            Port = GetFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _loop = Task.Run(ProcessRequestsAsync);
        }

        public int Port { get; }

        private async Task ProcessRequestsAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    var body = Encoding.UTF8.GetBytes("""{"apiVersion":"usvc-dev.developer.microsoft.com/v1","kind":"ContainerList","items":[]}""");
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.OutputStream.Close();
                }
                catch
                {
                    // Ignore failures writing the response; the test's retry loop will simply try again.
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
                // Best effort shutdown.
            }

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best effort shutdown.
            }

            _cts.Dispose();
        }
    }
}
