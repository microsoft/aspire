// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Cli.Certificates;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

public class DevCertsCheckTests
{
    private const int MinVersion = CertificateManager.CurrentAspNetCoreCertificateVersion;

    private static DevCertInfo CreateDevCertInfo(CertificateManager.TrustLevel trustLevel, string thumbprint, int version)
    {
        var now = DateTimeOffset.UtcNow;
        return new DevCertInfo
        {
            TrustLevel = trustLevel,
            Thumbprint = thumbprint,
            Version = version,
            ValidityNotBefore = now.AddDays(-30),
            ValidityNotAfter = now.AddDays(335),
            Subject = "CN=localhost",
            IsHttpsDevelopmentCertificate = true,
            IsExportable = true
        };
    }

    private static DevCertInfo CreateDevCertInfo(CertificateManager.TrustLevel trustLevel, X509Certificate2 certificate, int version)
    {
        return new DevCertInfo
        {
            TrustLevel = trustLevel,
            Thumbprint = certificate.Thumbprint,
            Version = version,
            ValidityNotBefore = certificate.NotBefore,
            ValidityNotAfter = certificate.NotAfter,
            Subject = certificate.Subject,
            IsHttpsDevelopmentCertificate = true,
            IsExportable = true
        };
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
    }

    private static string CreateCertUtil(DirectoryInfo directory)
    {
        var certUtilPath = Path.Combine(directory.FullName, CertificateHelpers.CertUtilCommand);
        File.WriteAllText(certUtilPath, "");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(certUtilPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        return certUtilPath;
    }

    [Fact]
    public void EvaluateCertificateResults_NoCertificates_ReturnsWarning()
    {
        var results = DevCertsCheck.EvaluateCertificateResults([], new HostEnvironment());

        var devCertsResult = Assert.Single(results);
        Assert.Equal(DevCertsCheck.CheckName, devCertsResult.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, devCertsResult.Status);
        Assert.Contains("No HTTPS development certificate found", devCertsResult.Message);
    }

    [Fact]
    public void EvaluateCertificateResults_MultipleCerts_AllTrusted_ReturnsPass()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "AAAA1111BBBB2222", MinVersion),
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "CCCC3333DDDD4444", MinVersion),
        };

        var results = DevCertsCheck.EvaluateCertificateResults(certs, new HostEnvironment());

        var devCertsResult = Assert.Single(results, r => r.Name == DevCertsCheck.CheckName);
        Assert.Equal(EnvironmentCheckStatus.Pass, devCertsResult.Status);
        Assert.Contains("trusted", devCertsResult.Message);
    }

    [Fact]
    public void EvaluateCertificateResults_MultipleCerts_NoneTrusted_ReturnsWarning()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.None, "AAAA1111BBBB2222", MinVersion),
            CreateDevCertInfo(CertificateManager.TrustLevel.None, "CCCC3333DDDD4444", MinVersion),
        };

        var results = DevCertsCheck.EvaluateCertificateResults(certs, new HostEnvironment());

        var devCertsResult = Assert.Single(results, r => r.Name == DevCertsCheck.CheckName);
        Assert.Equal(EnvironmentCheckStatus.Warning, devCertsResult.Status);
        Assert.Contains("none are trusted", devCertsResult.Message);
    }

    [Fact]
    public void EvaluateCertificateResults_MultipleCerts_SomeUntrusted_ReturnsWarning()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "AAAA1111BBBB2222", MinVersion),
            CreateDevCertInfo(CertificateManager.TrustLevel.None, "CCCC3333DDDD4444", MinVersion),
        };

        var results = DevCertsCheck.EvaluateCertificateResults(certs, new HostEnvironment());

        var devCertsResult = Assert.Single(results, r => r.Name == DevCertsCheck.CheckName);
        Assert.Equal(EnvironmentCheckStatus.Warning, devCertsResult.Status);
        Assert.Contains("Multiple HTTPS development certificates found", devCertsResult.Message);
    }

    [Fact]
    public void EvaluateCertificateResults_SingleCert_Trusted_ReturnsPass()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "AAAA1111BBBB2222", MinVersion),
        };

        var results = DevCertsCheck.EvaluateCertificateResults(certs, new HostEnvironment());

        var devCertsResult = Assert.Single(results, r => r.Name == DevCertsCheck.CheckName);
        Assert.Equal(EnvironmentCheckStatus.Pass, devCertsResult.Status);
        Assert.Contains("trusted", devCertsResult.Message);
    }

    [Fact]
    public void EvaluateCertificateResults_SingleCert_Untrusted_ReturnsWarning()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.None, "AAAA1111BBBB2222", MinVersion),
        };

        var results = DevCertsCheck.EvaluateCertificateResults(certs, new HostEnvironment());

        var devCertsResult = Assert.Single(results, r => r.Name == DevCertsCheck.CheckName);
        Assert.Equal(EnvironmentCheckStatus.Warning, devCertsResult.Status);
        Assert.Contains("not trusted", devCertsResult.Message);
    }

    [Fact]
    public void EvaluateCertificateResults_SingleCert_PartiallyTrusted_ReturnsWarning()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.Partial, "AAAA1111BBBB2222", MinVersion),
        };

        var results = DevCertsCheck.EvaluateCertificateResults(certs, new HostEnvironment());

        var devCertsResult = Assert.Single(results, r => r.Name == DevCertsCheck.CheckName);
        Assert.Equal(EnvironmentCheckStatus.Warning, devCertsResult.Status);
        Assert.Contains("partially trusted", devCertsResult.Message);
    }

    [Fact]
    public void EvaluateCertificateResults_OldTrustedCert_ReturnsVersionWarning()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "AAAA1111BBBB2222", MinVersion - 1),
        };

        var results = DevCertsCheck.EvaluateCertificateResults(certs, new HostEnvironment());

        Assert.Equal(2, results.Count);
        var versionResult = Assert.Single(results, r => r.Name == DevCertsCheck.VersionCheckName);
        Assert.Equal(EnvironmentCheckStatus.Warning, versionResult.Status);
        Assert.Contains("older version", versionResult.Message);
    }

    [Fact]
    public void EvaluateCertificateResults_MultipleCerts_AllTrusted_NoVersionWarning()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "AAAA1111BBBB2222", MinVersion),
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "CCCC3333DDDD4444", MinVersion + 1),
        };

        var results = DevCertsCheck.EvaluateCertificateResults(certs, new HostEnvironment());

        // Should only have the pass result, no version warning
        var devCertsResult = Assert.Single(results);
        Assert.Equal(DevCertsCheck.CheckName, devCertsResult.Name);
        Assert.Equal(EnvironmentCheckStatus.Pass, devCertsResult.Status);
    }

    [Fact]
    public void EvaluateCertificateResults_MultipleCerts_AllPartiallyTrusted_ReturnsPass()
    {
        // Partially trusted counts as trusted (not None), so all certs are "trusted"
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.Partial, "AAAA1111BBBB2222", MinVersion),
            CreateDevCertInfo(CertificateManager.TrustLevel.Partial, "CCCC3333DDDD4444", MinVersion),
        };

        var results = DevCertsCheck.EvaluateCertificateResults(certs, new HostEnvironment());

        // Should not have a "Multiple certs" warning since all are trusted
        var devCertsResult = Assert.Single(results, r => r.Name == DevCertsCheck.CheckName);
        Assert.NotEqual(EnvironmentCheckStatus.Warning, devCertsResult.Status);
    }

    [Fact]
    public void EvaluateCertificateResults_ThreeCerts_TwoTrustedOneNot_ReturnsWarning()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "AAAA1111BBBB2222", MinVersion),
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "CCCC3333DDDD4444", MinVersion),
            CreateDevCertInfo(CertificateManager.TrustLevel.None, "EEEE5555FFFF6666", MinVersion),
        };

        var results = DevCertsCheck.EvaluateCertificateResults(certs, new HostEnvironment());

        var devCertsResult = Assert.Single(results, r => r.Name == DevCertsCheck.CheckName);
        Assert.Equal(EnvironmentCheckStatus.Warning, devCertsResult.Status);
        Assert.Contains("3 certificates", devCertsResult.Message);
    }

    [Fact]
    public void EvaluateCertificateResults_PassResult_IncludesMetadata()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "AAAA1111BBBB2222", MinVersion),
        };

        var results = DevCertsCheck.EvaluateCertificateResults(certs, new HostEnvironment());

        var devCertsResult = Assert.Single(results, r => r.Name == DevCertsCheck.CheckName);
        Assert.NotNull(devCertsResult.Metadata);
        Assert.True(devCertsResult.Metadata.ContainsKey("certificates"));

        var certificates = devCertsResult.Metadata["certificates"]!.AsArray();
        Assert.Single(certificates);

        var certNode = certificates[0]!.AsObject();
        Assert.Equal("AAAA1111BBBB2222", certNode["thumbprint"]!.GetValue<string>());
        Assert.Equal(MinVersion, certNode["version"]!.GetValue<int>());
        Assert.Equal("full", certNode["trustLevel"]!.GetValue<string>());
        Assert.NotNull(certNode["notBefore"]);
        Assert.NotNull(certNode["notAfter"]);
    }

    [Fact]
    public void EvaluateCertificateResults_NoCertificates_DoesNotIncludeMetadata()
    {
        var results = DevCertsCheck.EvaluateCertificateResults([], new HostEnvironment());

        var devCertsResult = Assert.Single(results);
        Assert.Null(devCertsResult.Metadata);
    }

    [Fact]
    public async Task CheckAsync_LinuxWithoutCertUtil_ReturnsCertUtilWarning()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "AAAA1111BBBB2222", MinVersion),
        };
        var toolRunner = new TestCertificateToolRunner
        {
            CheckHttpCertificateCallback = () => new CertificateTrustResult
            {
                HasCertificates = true,
                TrustLevel = CertificateManager.TrustLevel.Full,
                Certificates = certs
            }
        };
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
        {
            ["PATH"] = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"))
        });
        var check = new DevCertsCheck(NullLogger<DevCertsCheck>.Instance, toolRunner, environment);

        var results = await check.CheckAsync();

        var certUtilResult = Assert.Single(results, r => r.Name == DevCertsCheck.CertUtilCheckName);
        Assert.Equal(EnvironmentCheckStatus.Warning, certUtilResult.Status);
        Assert.Contains("certutil", certUtilResult.Message);
    }

    [Fact]
    public async Task CheckAsync_LinuxWithCertUtil_DoesNotReturnCertUtilWarning()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            CreateCertUtil(tempDirectory);

            var certs = new List<DevCertInfo>
            {
                CreateDevCertInfo(CertificateManager.TrustLevel.Full, "AAAA1111BBBB2222", MinVersion),
            };
            var toolRunner = new TestCertificateToolRunner
            {
                CheckHttpCertificateCallback = () => new CertificateTrustResult
                {
                    HasCertificates = true,
                    TrustLevel = CertificateManager.TrustLevel.Full,
                    Certificates = certs
                }
            };
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = tempDirectory.FullName
            });
            var check = new DevCertsCheck(NullLogger<DevCertsCheck>.Instance, toolRunner, environment);

            var results = await check.CheckAsync();

            Assert.DoesNotContain(results, r => r.Name == DevCertsCheck.CertUtilCheckName);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_LinuxWithMatchingOpenSslCertificateCache_DoesNotReturnOpenSslCertificateCacheWarning()
    {
        using var certificate = CreateCertificate();
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            CreateCertUtil(tempDirectory);
            var trustDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "trust"));
            File.WriteAllText(Path.Combine(trustDirectory.FullName, "localhost.pem"), certificate.ExportCertificatePem());

            var certs = new List<DevCertInfo>
            {
                CreateDevCertInfo(CertificateManager.TrustLevel.Full, certificate, MinVersion),
            };
            var toolRunner = new TestCertificateToolRunner
            {
                CheckHttpCertificateCallback = () => new CertificateTrustResult
                {
                    HasCertificates = true,
                    TrustLevel = CertificateManager.TrustLevel.Full,
                    Certificates = certs
                }
            };
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = tempDirectory.FullName,
                [CertificateHelpers.DevCertsOpenSslCertDirEnvVar] = trustDirectory.FullName
            });
            var check = new DevCertsCheck(NullLogger<DevCertsCheck>.Instance, toolRunner, environment);

            var results = await check.CheckAsync();

            Assert.DoesNotContain(results, r => r.Name == DevCertsCheck.OpenSslCertificateCacheCheckName);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_LinuxWithCorruptOpenSslCertificateCache_ReturnsOpenSslCertificateCacheWarning()
    {
        using var certificate = CreateCertificate();
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            CreateCertUtil(tempDirectory);
            var trustDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "trust"));
            File.WriteAllText(Path.Combine(trustDirectory.FullName, "broken.pem"), "not a certificate");

            var certs = new List<DevCertInfo>
            {
                CreateDevCertInfo(CertificateManager.TrustLevel.Full, certificate, MinVersion),
            };
            var toolRunner = new TestCertificateToolRunner
            {
                CheckHttpCertificateCallback = () => new CertificateTrustResult
                {
                    HasCertificates = true,
                    TrustLevel = CertificateManager.TrustLevel.Full,
                    Certificates = certs
                }
            };
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = tempDirectory.FullName,
                [CertificateHelpers.DevCertsOpenSslCertDirEnvVar] = trustDirectory.FullName
            });
            var check = new DevCertsCheck(NullLogger<DevCertsCheck>.Instance, toolRunner, environment);

            var results = await check.CheckAsync();

            var cacheResult = Assert.Single(results, r => r.Name == DevCertsCheck.OpenSslCertificateCacheCheckName);
            Assert.Equal(EnvironmentCheckStatus.Warning, cacheResult.Status);
            Assert.Contains("broken.pem", cacheResult.Details);
            Assert.Contains("aspire certs clean", cacheResult.Fix);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_LinuxWithUntrustedCertificateAndCorruptOpenSslCertificateCache_ReturnsOpenSslCertificateCacheWarning()
    {
        using var certificate = CreateCertificate();
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            CreateCertUtil(tempDirectory);
            var trustDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "trust"));
            File.WriteAllText(Path.Combine(trustDirectory.FullName, "broken.pem"), "not a certificate");

            var certs = new List<DevCertInfo>
            {
                CreateDevCertInfo(CertificateManager.TrustLevel.None, certificate, MinVersion),
            };
            var toolRunner = new TestCertificateToolRunner
            {
                CheckHttpCertificateCallback = () => new CertificateTrustResult
                {
                    HasCertificates = true,
                    TrustLevel = CertificateManager.TrustLevel.None,
                    Certificates = certs
                }
            };
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = tempDirectory.FullName,
                [CertificateHelpers.DevCertsOpenSslCertDirEnvVar] = trustDirectory.FullName
            });
            var check = new DevCertsCheck(NullLogger<DevCertsCheck>.Instance, toolRunner, environment);

            var results = await check.CheckAsync();

            var cacheResult = Assert.Single(results, r => r.Name == DevCertsCheck.OpenSslCertificateCacheCheckName);
            Assert.Equal(EnvironmentCheckStatus.Warning, cacheResult.Status);
            Assert.Contains("broken.pem", cacheResult.Details);
            Assert.Contains("aspire certs clean", cacheResult.Fix);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_LinuxWithStaleOpenSslCertificateCache_ReturnsOpenSslCertificateCacheWarning()
    {
        using var certificate = CreateCertificate();
        using var staleCertificate = CreateCertificate();
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            CreateCertUtil(tempDirectory);
            var trustDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "trust"));
            File.WriteAllText(Path.Combine(trustDirectory.FullName, "stale.pem"), staleCertificate.ExportCertificatePem());

            var certs = new List<DevCertInfo>
            {
                CreateDevCertInfo(CertificateManager.TrustLevel.Full, certificate, MinVersion),
            };
            var toolRunner = new TestCertificateToolRunner
            {
                CheckHttpCertificateCallback = () => new CertificateTrustResult
                {
                    HasCertificates = true,
                    TrustLevel = CertificateManager.TrustLevel.Full,
                    Certificates = certs
                }
            };
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = tempDirectory.FullName,
                [CertificateHelpers.DevCertsOpenSslCertDirEnvVar] = trustDirectory.FullName
            });
            var check = new DevCertsCheck(NullLogger<DevCertsCheck>.Instance, toolRunner, environment);

            var results = await check.CheckAsync();

            var cacheResult = Assert.Single(results, r => r.Name == DevCertsCheck.OpenSslCertificateCacheCheckName);
            Assert.Equal(EnvironmentCheckStatus.Warning, cacheResult.Status);
            Assert.Contains(certificate.Thumbprint, cacheResult.Details);
            Assert.Contains("aspire certs clean", cacheResult.Fix);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_NonLinux_DoesNotReadEnvironmentVariablesForCertUtilWarning()
    {
        var certs = new List<DevCertInfo>
        {
            CreateDevCertInfo(CertificateManager.TrustLevel.Full, "AAAA1111BBBB2222", MinVersion),
        };
        var toolRunner = new TestCertificateToolRunner
        {
            CheckHttpCertificateCallback = () => new CertificateTrustResult
            {
                HasCertificates = true,
                TrustLevel = CertificateManager.TrustLevel.Full,
                Certificates = certs
            }
        };
        var environment = TestEnvironment.CreateMacOS(new Dictionary<string, string?>
        {
            ["PATH"] = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"))
        });
        var check = new DevCertsCheck(NullLogger<DevCertsCheck>.Instance, toolRunner, environment);

        var results = await check.CheckAsync();

        Assert.Equal(0, environment.GetEnvironmentVariablesCallCount);
        Assert.Collection(results, result => Assert.Equal(DevCertsCheck.CheckName, result.Name));
    }
}
