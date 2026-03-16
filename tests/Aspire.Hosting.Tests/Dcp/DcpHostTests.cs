// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Tests.Dcp;

#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIRECERTIFICATES001

[Trait("Partition", "4")]
public sealed class DcpHostTests
{
    private static Locations CreateTestLocations()
    {
        var directoryService = new FileSystemService(new ConfigurationBuilder().Build());
        return new Locations(directoryService);
    }

    private static X509Certificate2 LoadTestCertificate()
    {
        var searchPaths = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "tests", "Shared", "TestCertificates", "testCert.pfx"),
            Path.Combine(AppContext.BaseDirectory, "shared", "TestCertificates", "testCert.pfx"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "Shared", "TestCertificates", "testCert.pfx"))
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                return new X509Certificate2(path, "testPassword", X509KeyStorageFlags.Exportable);
            }
        }

        throw new FileNotFoundException("Could not locate test certificate file 'testCert.pfx' in expected locations.");
    }

    private static DcpHost CreateDcpHost(
        IDeveloperCertificateService? developerCertificateService = null,
        Locations? locations = null,
        IConfiguration? configuration = null)
    {
        var loggerFactory = new NullLoggerFactory();
        var dcpOptions = Options.Create(new DcpOptions());
        var dependencyCheckService = new TestDcpDependencyCheckService();
        var interactionService = new TestInteractionService();
        locations ??= CreateTestLocations();
        var applicationModel = new DistributedApplicationModel(new ResourceCollection());
        var timeProvider = new FakeTimeProvider();
        developerCertificateService ??= new TestDeveloperCertificateService([], false, false, false);
        configuration ??= new ConfigurationBuilder().Build();
        var fileSystemService = new FileSystemService(configuration);

        return new DcpHost(
            loggerFactory,
            dcpOptions,
            dependencyCheckService,
            interactionService,
            locations,
            applicationModel,
            timeProvider,
            developerCertificateService,
            fileSystemService,
            configuration);
    }

    private static IConfiguration CreateConfigWithDcpTlsEnabled()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.DcpDeveloperCertificate] = "true"
            })
            .Build();
    }

    [Fact]
    public async Task PrepareDcpTlsCertificateAsync_WithCertAvailable_WritesCertAndKeyFiles()
    {
        using var cert = LoadTestCertificate();
        var certService = new TestDeveloperCertificateService([cert], false, false, false);
        var locations = CreateTestLocations();
        var dcpHost = CreateDcpHost(developerCertificateService: certService, locations: locations, configuration: CreateConfigWithDcpTlsEnabled());

        await dcpHost.PrepareDcpTlsCertificateAsync(CancellationToken.None).DefaultTimeout();

        var certFilePath = Path.Combine(locations.DcpSessionDir, "dcp-tls.crt");
        var keyFilePath = Path.Combine(locations.DcpSessionDir, "dcp-tls.key");

        Assert.True(File.Exists(certFilePath), "Certificate PEM file should exist.");
        Assert.True(File.Exists(keyFilePath), "Key PEM file should exist.");

        // Verify the cert PEM content can be loaded back
        var certPem = File.ReadAllText(certFilePath);
        Assert.Contains("BEGIN CERTIFICATE", certPem);
        Assert.Contains("END CERTIFICATE", certPem);

        // Verify the key PEM content can be loaded back
        var keyPem = File.ReadAllText(keyFilePath);
        Assert.Contains("BEGIN PRIVATE KEY", keyPem);
        Assert.Contains("END PRIVATE KEY", keyPem);
    }

    [Fact]
    public async Task PrepareDcpTlsCertificateAsync_WithNoCerts_DoesNotWriteFiles()
    {
        var certService = new TestDeveloperCertificateService([], false, false, false);
        var locations = CreateTestLocations();
        var dcpHost = CreateDcpHost(developerCertificateService: certService, locations: locations, configuration: CreateConfigWithDcpTlsEnabled());

        await dcpHost.PrepareDcpTlsCertificateAsync(CancellationToken.None).DefaultTimeout();

        var certFilePath = Path.Combine(locations.DcpSessionDir, "dcp-tls.crt");
        var keyFilePath = Path.Combine(locations.DcpSessionDir, "dcp-tls.key");

        Assert.False(File.Exists(certFilePath), "Certificate PEM file should not exist.");
        Assert.False(File.Exists(keyFilePath), "Key PEM file should not exist.");
    }

    [Fact]
    public async Task PrepareDcpTlsCertificateAsync_CertPemMatchesOriginalThumbprint()
    {
        using var originalCert = LoadTestCertificate();
        var certService = new TestDeveloperCertificateService([originalCert], false, false, false);
        var locations = CreateTestLocations();
        var dcpHost = CreateDcpHost(developerCertificateService: certService, locations: locations, configuration: CreateConfigWithDcpTlsEnabled());

        await dcpHost.PrepareDcpTlsCertificateAsync(CancellationToken.None).DefaultTimeout();

        var certFilePath = Path.Combine(locations.DcpSessionDir, "dcp-tls.crt");
        var keyFilePath = Path.Combine(locations.DcpSessionDir, "dcp-tls.key");

        // Verify the public cert PEM matches the original certificate thumbprint
        var certPem = File.ReadAllText(certFilePath);
        using var loadedCert = X509Certificate2.CreateFromPem(certPem);
        Assert.Equal(originalCert.Thumbprint, loadedCert.Thumbprint);

        // Verify the key PEM is a valid unencrypted PKCS8 private key
        var keyPem = File.ReadAllText(keyFilePath);
        Assert.Contains("BEGIN PRIVATE KEY", keyPem);
        Assert.DoesNotContain("ENCRYPTED", keyPem);
    }

    [Fact]
    public async Task PrepareDcpTlsCertificateAsync_DefaultBehavior_SkipsCertExport()
    {
        using var cert = LoadTestCertificate();
        var certService = new TestDeveloperCertificateService([cert], false, false, false);
        var locations = CreateTestLocations();
        var dcpHost = CreateDcpHost(developerCertificateService: certService, locations: locations);

        await dcpHost.PrepareDcpTlsCertificateAsync(CancellationToken.None).DefaultTimeout();

        var certFilePath = Path.Combine(locations.DcpSessionDir, "dcp-tls.crt");
        var keyFilePath = Path.Combine(locations.DcpSessionDir, "dcp-tls.key");

        Assert.False(File.Exists(certFilePath), "Certificate PEM file should not exist when DCP TLS flag is not set.");
        Assert.False(File.Exists(keyFilePath), "Key PEM file should not exist when DCP TLS flag is not set.");
    }
}
