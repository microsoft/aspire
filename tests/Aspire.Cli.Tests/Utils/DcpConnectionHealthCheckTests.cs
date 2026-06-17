// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Cli.Layout;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Shared;
using Aspire.TestUtilities;
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
        var tester = new TestDcpConnectionTester
        {
            TestConnectionAsyncCallback = (_, _, _) => throw new InvalidOperationException("Should not be called.")
        };
        var check = new DcpConnectionHealthCheck(
            new TestLayoutDiscovery(dcpPath: null),
            tester,
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionHealthCheck>.Instance);

        var result = Assert.Single(await check.CheckAsync());

        Assert.Equal("dcp-bundle", result.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
    }

    [Fact]
    public async Task CheckAsync_WhenDcpExecutableIsMissing_ReturnsFailure()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        var check = new DcpConnectionHealthCheck(
            new TestLayoutDiscovery(dcpDirectory.FullName),
            new TestDcpConnectionTester(),
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionHealthCheck>.Instance);

        var result = Assert.Single(await check.CheckAsync());

        Assert.Equal("dcp-bundle", result.Name);
        Assert.Equal(EnvironmentCheckStatus.Fail, result.Status);
        Assert.Contains("DCP executable not found", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_WhenDcpConnectionsSucceed_ReturnsOneResultForEachCertificateMode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dcpDirectory = GetRestoredDcpDirectory();
        var seenModes = new List<DcpConnectionSecurityMode>();
        var tester = new TestDcpConnectionTester
        {
            TestConnectionAsyncCallback = (path, mode, _) =>
            {
                Assert.Equal(dcpDirectory.FullName, path);
                seenModes.Add(mode);
                return Task.FromResult(new DcpConnectionTestResult(mode, EnvironmentCheckStatus.Pass, $"{mode} passed"));
            }
        };
        var check = new DcpConnectionHealthCheck(
            new TestLayoutDiscovery(dcpDirectory.FullName),
            tester,
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionHealthCheck>.Instance);

        var results = await check.CheckAsync();

        Assert.Collection(results,
            result =>
            {
                Assert.Equal("dcp-ephemeral-certificate", result.Name);
                Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
            },
            result =>
            {
                Assert.Equal("dcp-developer-certificate", result.Name);
                Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
            });
        Assert.Equal([DcpConnectionSecurityMode.EphemeralCertificate, DcpConnectionSecurityMode.DeveloperCertificate], seenModes);
    }

    [Fact]
    public async Task CheckAsync_WhenDeveloperCertificateConnectionFails_ReturnsFailureWithFix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dcpDirectory = GetRestoredDcpDirectory();
        var tester = new TestDcpConnectionTester
        {
            TestConnectionAsyncCallback = (_, mode, _) => Task.FromResult(mode switch
            {
                DcpConnectionSecurityMode.EphemeralCertificate => new DcpConnectionTestResult(mode, EnvironmentCheckStatus.Pass, "ephemeral passed"),
                _ => new DcpConnectionTestResult(mode, EnvironmentCheckStatus.Fail, "dev cert failed", "TLS failed", "Run `aspire certs trust`.")
            })
        };
        var check = new DcpConnectionHealthCheck(
            new TestLayoutDiscovery(dcpDirectory.FullName),
            tester,
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionHealthCheck>.Instance);

        var results = await check.CheckAsync();

        var developerCertificateResult = results.Single(result => result.Name == "dcp-developer-certificate");
        Assert.Equal(EnvironmentCheckStatus.Fail, developerCertificateResult.Status);
        Assert.Equal("TLS failed", developerCertificateResult.Details);
        Assert.Equal("Run `aspire certs trust`.", developerCertificateResult.Fix);
    }

    [Fact]
    public async Task DcpConnectionTester_WhenUsingEphemeralCertificate_ConnectsToRestoredDcp()
    {
        var result = await TestRealDcpConnectionAsync(DcpConnectionSecurityMode.EphemeralCertificate);

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
    }

    [Fact]
    [RequiresFeature(TestFeature.DevCert)]
    public async Task DcpConnectionTester_WhenUsingDeveloperCertificate_ConnectsToRestoredDcp()
    {
        var result = await TestRealDcpConnectionAsync(DcpConnectionSecurityMode.DeveloperCertificate);

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
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
    public void DcpDeveloperCertificateCache_WhenCachedKeyExists_ReturnsCachedKeyAndPublicCertificatePath()
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

            var lookup = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(certificate.Thumbprint)));
            var cacheDirectory = Path.Combine(homePath, ".aspire", "dev-certs", "https");
            Directory.CreateDirectory(cacheDirectory);
            var certificatePath = Path.Combine(cacheDirectory, $"{lookup}.crt");
            var keyPath = Path.Combine(cacheDirectory, $"{lookup}.key");
            File.WriteAllText(keyPath, "cached key");

            var paths = DcpDeveloperCertificateCache.TryPrepareCertificateFilePaths(certificate);

            Assert.NotNull(paths);
            Assert.Equal(certificatePath, paths.Value.CertificatePath);
            Assert.Equal(certificate.ExportCertificatePem(), File.ReadAllText(certificatePath));
            Assert.Equal(keyPath, paths.Value.KeyPath);
        }, homeDirectory.Path, options).Dispose();
    }

    private async Task<DcpConnectionTestResult> TestRealDcpConnectionAsync(DcpConnectionSecurityMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dcpDirectory = GetRestoredDcpDirectory();
        var certificateManager = CertificateManager.Create(NullLogger.Instance);
        var tester = new DcpConnectionTester(
            certificateManager,
            CreateExecutionContext(workspace),
            NullLogger<DcpConnectionTester>.Instance);

        return await tester.TestConnectionAsync(dcpDirectory.FullName, mode, TestContext.Current.CancellationToken);
    }

    private static DirectoryInfo GetRestoredDcpDirectory()
    {
        var dcpDirectoryPath = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => string.Equals(attribute.Key, "AspireTestDcpDir", StringComparison.Ordinal))
            .Value;

        Assert.False(string.IsNullOrWhiteSpace(dcpDirectoryPath));

        var dcpDirectory = new DirectoryInfo(dcpDirectoryPath!);
        var dcpExecutablePath = BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName);
        Assert.True(File.Exists(dcpExecutablePath), $"Expected restored DCP executable at '{dcpExecutablePath}'. Run ./restore.sh before running these tests.");

        return dcpDirectory;
    }

    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace) =>
        new(
            workingDirectory: workspace.WorkspaceRoot,
            hivesDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-hives"),
            cacheDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-cache"),
            sdksDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-sdks"),
            logsDirectory: workspace.WorkspaceRoot.CreateSubdirectory(".aspire-logs"),
            logFilePath: "test.log");

    private sealed class TestLayoutDiscovery(string? dcpPath) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => null;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null) =>
            component == LayoutComponent.Dcp ? dcpPath : null;

        public bool IsBundleModeAvailable(string? projectDirectory = null) => dcpPath is not null;
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
