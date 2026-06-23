// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tester = new TestDcpConnectionChecker
        {
            TestConnectionAsyncCallback = (_, _, _) => throw new InvalidOperationException("Should not be called.")
        };
        var check = new DcpConnectionHealthCheck(
            new NullLayoutDiscovery(),
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        var check = new DcpConnectionHealthCheck(
            new FixedLayoutDiscovery(LayoutComponent.Dcp, dcpDirectory.FullName),
            new TestDcpConnectionChecker(),
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionHealthCheck>.Instance);

        var result = Assert.Single(await check.CheckAsync());

        Assert.Equal(DcpConnectionHealthCheck.BundleCheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Contains("Developer Control Plane (DCP) executable not found", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_WhenDcpConnectionsSucceed_ReturnsSingleSuccessResult()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        using var homeDirectory = new TestTempDirectory();
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["HOME"] = homeDirectory.Path;
        options.StartInfo.Environment["USERPROFILE"] = homeDirectory.Path;

        RemoteExecutor.Invoke(static homePath =>
        {
            var certificateManager = CertificateManager.Create(NullLogger.Instance);
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
        }, homeDirectory.Path, options).Dispose();
    }

    private static DirectoryInfo CreateDcpDirectoryWithExecutable(TemporaryWorkspace workspace)
    {
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
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

    private static string CreateTestCertificatePem()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=dcp-test-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        return certificate.ExportCertificatePem();
    }
}
