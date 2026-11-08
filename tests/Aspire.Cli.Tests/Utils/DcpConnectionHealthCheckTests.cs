// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Cli.Certificates;
using Aspire.Cli.Layout;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Shared;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

public class DcpConnectionHealthCheckTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CheckAsync_WhenNoDcpBundleIsDiscovered_ReturnsWarningAndSkipsConnectionTests()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var tester = new TestDcpConnectionChecker
        {
            TestConnectionAsyncCallback = (_, _, _) => throw new InvalidOperationException("Should not be called.")
        };
        var check = new DcpConnectionHealthCheck(
            new NullLayoutDiscovery(),
            new NullBundleService(),
            tester,
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionHealthCheck>.Instance);

        var result = Assert.Single(await check.CheckAsync());

        Assert.Equal(DcpConnectionHealthCheck.BundleCheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
    }

    [Fact]
    public async Task CheckAsync_WhenDcpExecutableIsMissing_ReturnsFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        var check = new DcpConnectionHealthCheck(
            new FixedLayoutDiscovery(LayoutComponent.Dcp, dcpDirectory.FullName),
            new NullBundleService(),
            new TestDcpConnectionChecker(),
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionHealthCheck>.Instance);

        var result = Assert.Single(await check.CheckAsync());

        Assert.Equal(DcpConnectionHealthCheck.BundleCheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Contains("Developer Control Plane (DCP) executable not found", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_WhenNoLayoutIsDiscovered_ExtractsBundleBeforeProbingForDcp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = CreateDcpDirectoryWithExecutable(workspace);
        var probedPaths = new List<string>();
        var tester = new TestDcpConnectionChecker
        {
            TestConnectionAsyncCallback = (path, useDeveloperCertificate, _) =>
            {
                probedPaths.Add(path);
                return Task.FromResult(TestDcpConnectionChecker.CreateResult(useDeveloperCertificate, EnvironmentCheckStatus.Pass, "passed"));
            }
        };

        // On a fresh install nothing has extracted the bundle yet, so discovery finds no layout. The check has
        // to extract it rather than report DCP as missing.
        var bundleService = new RecordingBundleService(dcpDirectory.FullName);
        var check = new DcpConnectionHealthCheck(
            new NullLayoutDiscovery(),
            bundleService,
            tester,
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionHealthCheck>.Instance);

        var result = Assert.Single(await check.CheckAsync());

        Assert.Equal(1, bundleService.EnsureExtractedAndAcquireLayoutCallCount);
        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.All(probedPaths, path => Assert.Equal(dcpDirectory.FullName, path));
        Assert.NotEmpty(probedPaths);
    }

    [Fact]
    public async Task CheckAsync_PrefersDiscoveredLayoutOverExtraction()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var discoveredDcpDirectory = CreateDcpDirectoryWithExecutable(workspace, "discovered-dcp");
        var extractedDcpDirectory = CreateDcpDirectoryWithExecutable(workspace, "extracted-dcp");
        var probedPaths = new List<string>();
        var tester = new TestDcpConnectionChecker
        {
            TestConnectionAsyncCallback = (path, useDeveloperCertificate, _) =>
            {
                probedPaths.Add(path);
                return Task.FromResult(TestDcpConnectionChecker.CreateResult(useDeveloperCertificate, EnvironmentCheckStatus.Pass, "passed"));
            }
        };

        // Discovery honors ASPIRE_DCP_PATH and already extracted bundles, so whatever it returns must win.
        var bundleService = new RecordingBundleService(extractedDcpDirectory.FullName);
        var check = new DcpConnectionHealthCheck(
            new FixedLayoutDiscovery(LayoutComponent.Dcp, discoveredDcpDirectory.FullName),
            bundleService,
            tester,
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionHealthCheck>.Instance);

        await check.CheckAsync();

        Assert.Equal(0, bundleService.EnsureExtractedAndAcquireLayoutCallCount);
        Assert.All(probedPaths, path => Assert.Equal(discoveredDcpDirectory.FullName, path));
        Assert.NotEmpty(probedPaths);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenStartIsCanceledBeforeProcessStarts_PropagatesCancellationAndDisposesExecution()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = CreateDcpDirectoryWithExecutable(workspace);
        TestProcessExecution? execution = null;
        var processExecutionFactory = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (fileName, args, env, _, options) =>
            {
                var testExecution = new TestProcessExecution(
                    fileName,
                    args,
                    env,
                    options,
                    (_, _, _) => Task.FromResult((0, (string?)null)),
                    () => 1)
                {
                    ThrowOnHasExitedBeforeStart = true
                };

                execution = testExecution;
                return testExecution;
            }
        };
        var checker = new DcpConnectionChecker(
            CertificateManager.Create(NullLogger.Instance, new HostEnvironment(), nssDbOverride: null),
            processExecutionFactory,
            CreateExecutionContext(workspace),
            new HostEnvironment(),
            NullLogger<DcpConnectionChecker>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => checker.TestConnectionAsync(dcpDirectory.FullName, useDeveloperCertificate: false, cancellation.Token));

        Assert.NotNull(execution);
        Assert.False(execution.Started);
        Assert.Equal(0, execution.KillCount);
        Assert.Equal(1, execution.DisposeCount);
    }

    [Fact]
    public async Task TestConnectionAsync_StopsDcpGracefullyInsteadOfKillingIt()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = CreateDcpDirectoryWithExecutable(workspace);
        await using var dcpApiServer = FakeDcpApiServer.Start();
        TestProcessExecution? execution = null;
        var processExecutionFactory = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (fileName, args, env, _, options) =>
            {
                var kubeconfigPath = args[Array.IndexOf(args, "--kubeconfig") + 1];
                File.WriteAllText(kubeconfigPath, $$"""
                    apiVersion: v1
                    kind: Config
                    clusters:
                    - name: dcp
                      cluster:
                        server: "{{dcpApiServer.Address}}"
                    """);

                execution = new TestProcessExecution(
                    fileName,
                    args,
                    env,
                    options,
                    (_, _, _) => Task.FromResult((0, (string?)null)),
                    () => 1)
                {
                    // Like DCP, the fake process only exits on its own after the stop request is accepted.
                    WaitForExitAsyncCallback = (_, cancellationToken) => dcpApiServer.StopAccepted.WaitAsync(cancellationToken)
                };
                return execution;
            }
        };
        var checker = new DcpConnectionChecker(
            CertificateManager.Create(NullLogger.Instance, new HostEnvironment(), nssDbOverride: null),
            processExecutionFactory,
            CreateExecutionContext(workspace),
            new HostEnvironment(),
            NullLogger<DcpConnectionChecker>.Instance);

        var result = await checker.TestConnectionAsync(dcpDirectory.FullName, useDeveloperCertificate: false, TestContext.Current.CancellationToken);

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal(["application/merge-patch+json"], dcpApiServer.StopRequestContentTypes);
        Assert.NotNull(execution);
        Assert.True(execution.HasExited);
        Assert.Equal(0, execution.KillCount);
        Assert.Equal(1, execution.DisposeCount);
    }

    [Fact]
    public async Task CheckAsync_WhenDcpConnectionsSucceed_ReturnsSingleSuccessResult()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = CreateDcpDirectoryWithExecutable(workspace);
        var seenUseDeveloperCertificateValues = new ConcurrentBag<bool>();
        var tester = new TestDcpConnectionChecker
        {
            TestConnectionAsyncCallback = (path, useDeveloperCertificate, _) =>
            {
                Assert.Equal(dcpDirectory.FullName, path);
                seenUseDeveloperCertificateValues.Add(useDeveloperCertificate);
                return Task.FromResult(TestDcpConnectionChecker.CreateResult(useDeveloperCertificate, EnvironmentCheckStatus.Pass, $"useDeveloperCertificate={useDeveloperCertificate} passed"));
            }
        };
        var check = new DcpConnectionHealthCheck(
            new FixedLayoutDiscovery(LayoutComponent.Dcp, dcpDirectory.FullName),
            new NullBundleService(),
            tester,
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionHealthCheck>.Instance);

        var results = await check.CheckAsync();

        var result = Assert.Single(results);
        Assert.Equal(DcpConnectionHealthCheck.ConnectionCheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Contains("Developer Control Plane (DCP) connection health checks succeeded", result.Message, StringComparison.Ordinal);
        Assert.Equal(
            [false, true],
            seenUseDeveloperCertificateValues.Order().ToArray());
    }

    [Fact]
    public async Task CheckAsync_WhenDeveloperCertificateConnectionFails_ReturnsFailureWithFix()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var dcpDirectory = CreateDcpDirectoryWithExecutable(workspace);
        var tester = new TestDcpConnectionChecker
        {
            TestConnectionAsyncCallback = (_, useDeveloperCertificate, _) => Task.FromResult(
                useDeveloperCertificate
                    ? TestDcpConnectionChecker.CreateResult(useDeveloperCertificate, EnvironmentCheckStatus.Fail, "dev cert failed", "TLS failed", "Run `aspire certs trust`.")
                    : TestDcpConnectionChecker.CreateResult(useDeveloperCertificate, EnvironmentCheckStatus.Pass, "ephemeral passed"))
        };
        var check = new DcpConnectionHealthCheck(
            new FixedLayoutDiscovery(LayoutComponent.Dcp, dcpDirectory.FullName),
            new NullBundleService(),
            tester,
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionHealthCheck>.Instance);

        var results = await check.CheckAsync();

        var developerCertificateResult = results.Single(result => result.Name == DcpConnectionHealthCheck.DeveloperCertificateCheckName);
        Assert.Equal(EnvironmentCheckStatus.Fail, developerCertificateResult.Status);
        Assert.Equal("TLS failed", developerCertificateResult.Details);
        Assert.Equal("Run `aspire certs trust`.", developerCertificateResult.Fix);
    }

    [Fact]
    public void DcpKubeconfig_Parse_ReadsServerTokenAndCertificateData()
    {
        var authorityCertificateData = Convert.ToBase64String(Encoding.UTF8.GetBytes(CreateTestCertificatePem()));
        var kubeconfig = $$"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: dcp
              cluster:
                server: "https://127.0.0.1:12345"
                certificate-authority-data: {{authorityCertificateData}}
            users:
            - name: dcp
              user:
                token: dcp-test-token
            """;

        using var parsed = DcpKubeconfig.Parse(kubeconfig);

        Assert.Equal(new Uri("https://127.0.0.1:12345"), parsed.Server);
        Assert.Equal("dcp-test-token", parsed.Token);
        Assert.Single(parsed.CertificateAuthorityCertificates);
    }

    [Fact]
    public async Task DcpKubeconfig_ReadFileWithRetryAsync_RetriesWhenInitialContentIsPartial()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var kubeconfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "kubeconfig");
        await File.WriteAllTextAsync(kubeconfigPath, "clusters:", TestContext.Current.CancellationToken);

        var retryCount = 0;
        Task DelayAsync(TimeSpan _, CancellationToken cancellationToken)
        {
            retryCount++;
            var completeKubeconfig = """
                apiVersion: v1
                kind: Config
                clusters:
                - name: dcp
                  cluster:
                    server: "https://127.0.0.1:12345"
                """;

            return File.WriteAllTextAsync(kubeconfigPath, completeKubeconfig, cancellationToken);
        }

        using var parsed = await DcpKubeconfig.ReadFileWithRetryAsync(kubeconfigPath, DelayAsync, TestContext.Current.CancellationToken);

        Assert.Equal(1, retryCount);
        Assert.Equal(new Uri("https://127.0.0.1:12345"), parsed.Server);
    }

    [Fact]
    public void DcpDeveloperCertificateCache_WhenCachedKeyExists_WritesPublicCertificateToExistingCache()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only supported on Linux in CI.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["HOME"] = workspace.Path;
        options.StartInfo.Environment["USERPROFILE"] = workspace.Path;

        RemoteExecutor.Invoke(static homePath =>
        {
            var certificateManager = CertificateManager.Create(NullLogger.Instance, new HostEnvironment(), nssDbOverride: null);
            using var certificate = certificateManager.CreateAspNetCoreHttpsDevelopmentCertificate(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(30));

            var lookup = CertificateHelpers.GetAspireCertificateHash(certificate);
            var cacheDirectory = CertificateHelpers.AspireDevCertsHttpsCacheDirectory;
            Directory.CreateDirectory(cacheDirectory);
            var certificatePath = Path.Combine(cacheDirectory, $"{lookup}.crt");
            var keyPath = Path.Combine(cacheDirectory, $"{lookup}.key");
            File.WriteAllText(keyPath, "cached key");

            var cachedCertificatePath = DcpDeveloperCertificateCache.EnsureDeveloperCertificateCache(certificateManager, certificate);

            Assert.Equal(certificatePath, cachedCertificatePath);
            Assert.Equal(certificate.ExportCertificatePem(), File.ReadAllText(certificatePath));
            Assert.Equal("cached key", File.ReadAllText(keyPath));
        }, workspace.Path, options).Dispose();
    }

    private static DirectoryInfo CreateDcpDirectoryWithExecutable(TemporaryWorkspace workspace, string directoryName = "dcp")
    {
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory(directoryName);
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName), string.Empty);

        return dcpDirectory;
    }

    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace) =>
        new(
            workingDirectory: workspace.WorkspaceRoot,
            hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-hives"),
            cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-cache"),
            sdksDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-sdks"),
            logsDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-logs"),
            logFilePath: "test.log",
            identityChannel: "local");

    /// <summary>
    /// Minimal HTTP/1.1 stand-in for the DCP API server. Like DCP, it only accepts the stop PATCH when
    /// the Content-Type is exactly the merge-patch media type and answers 415 otherwise.
    /// </summary>
    private sealed class FakeDcpApiServer : IAsyncDisposable
    {
        private const string AcceptedStopMediaType = "application/merge-patch+json";
        private const int StopExitCode = 0;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly TaskCompletionSource<int> _stopAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _stopRequestContentTypes = new();
        private readonly Task _acceptLoop;

        private FakeDcpApiServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _acceptLoop = AcceptLoopAsync();
        }

        public Uri Address { get; }

        public Task<int> StopAccepted => _stopAccepted.Task;

        public string[] StopRequestContentTypes => _stopRequestContentTypes.ToArray();

        public static FakeDcpApiServer Start() => new();

        private async Task AcceptLoopAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                }
                catch (Exception) when (_stopping.IsCancellationRequested)
                {
                    return;
                }

                using (client)
                {
                    await HandleRequestAsync(client.GetStream()).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleRequestAsync(NetworkStream stream)
        {
            var buffer = new List<byte>();
            var readBuffer = new byte[4096];
            int headerEnd;
            while ((headerEnd = IndexOfHeaderTerminator(buffer)) < 0)
            {
                var read = await stream.ReadAsync(readBuffer, _stopping.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                buffer.AddRange(readBuffer.AsSpan(0, read));
            }

            var headerLines = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray()).Split("\r\n");
            var requestLine = headerLines[0].Split(' ');
            var headers = headerLines.Skip(1)
                .Select(line => line.Split(':', 2))
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

            var contentLength = headers.TryGetValue("Content-Length", out var length) ? int.Parse(length, CultureInfo.InvariantCulture) : 0;
            var bodyStart = headerEnd + 4;
            while (buffer.Count - bodyStart < contentLength)
            {
                var read = await stream.ReadAsync(readBuffer, _stopping.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                buffer.AddRange(readBuffer.AsSpan(0, read));
            }

            var statusLine = (requestLine[0], requestLine[1]) switch
            {
                ("GET", "/version") => "200 OK",
                ("PATCH", "/admin/execution") => HandleStopRequest(headers.GetValueOrDefault("Content-Type")),
                _ => "404 Not Found"
            };

            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {statusLine}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, _stopping.Token).ConfigureAwait(false);
        }

        private string HandleStopRequest(string? contentType)
        {
            _stopRequestContentTypes.Enqueue(contentType ?? string.Empty);
            if (!string.Equals(contentType, AcceptedStopMediaType, StringComparison.Ordinal))
            {
                return "415 Unsupported Media Type";
            }

            _stopAccepted.TrySetResult(StopExitCode);
            return "202 Accepted";
        }

        private static int IndexOfHeaderTerminator(List<byte> buffer)
        {
            for (var i = 0; i + 3 < buffer.Count; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Stop();
            await _acceptLoop.ConfigureAwait(false);
            _stopping.Dispose();
        }
    }

    private static string CreateTestCertificatePem()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=dcp-test-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        return certificate.ExportCertificatePem();
    }
}
