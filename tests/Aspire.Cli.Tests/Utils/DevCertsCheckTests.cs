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

    private static DevCertsCheck CreateCheck(TestCertificateToolRunner toolRunner, IEnvironment environment, TestProcessExecutionFactory? processExecutionFactory = null) =>
        new(NullLogger<DevCertsCheck>.Instance, toolRunner, environment, processExecutionFactory ?? CreateOpenSslProcessExecutionFactory());

    private static TestProcessExecutionFactory CreateOpenSslProcessExecutionFactory(string hash = "12345678") =>
        new()
        {
            AsyncAttemptCallback = (_, _, _) => Task.FromResult((0, (string?)hash))
        };

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

    private static string CreateOpenSsl(DirectoryInfo directory, string hash)
    {
        var openSslPath = OperatingSystem.IsWindows()
            ? Path.Combine(directory.FullName, "openssl.cmd")
            : Path.Combine(directory.FullName, "openssl");

        var contents = OperatingSystem.IsWindows()
            ? $"""
                @echo off
                if "%1"=="x509" (
                  echo {hash}
                  exit /b 0
                )
                exit /b 1
                """
            : $"""
                #!/bin/sh
                if [ "$1" = "x509" ]; then
                  echo {hash}
                  exit 0
                fi
                exit 1
                """;
        File.WriteAllText(openSslPath, contents);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(openSslPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        return openSslPath;
    }

    private static string GetOpenSslCertificateFileName(X509Certificate2 certificate) =>
        $"aspnetcore-localhost-{certificate.Thumbprint}.pem";

    private static void WriteOpenSslCertificateCache(DirectoryInfo trustDirectory, X509Certificate2 certificate, string hashEntryName = "12345678.0")
    {
        var certificatePem = certificate.ExportCertificatePem();
        File.WriteAllText(Path.Combine(trustDirectory.FullName, GetOpenSslCertificateFileName(certificate)), certificatePem);
        File.WriteAllText(Path.Combine(trustDirectory.FullName, hashEntryName), certificatePem);
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
        var check = CreateCheck(toolRunner, environment);

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
            var check = CreateCheck(toolRunner, environment);

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
            if (!OperatingSystem.IsWindows())
            {
                CreateOpenSsl(tempDirectory, "12345678");
            }

            var trustDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "trust"));
            WriteOpenSslCertificateCache(trustDirectory, certificate);

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
            var check = CreateCheck(toolRunner, environment);

            var results = await check.CheckAsync();

            Assert.DoesNotContain(results, r => r.Name == DevCertsCheck.OpenSslCertificateCacheCheckName);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "The synthetic openssl command uses a POSIX shell script.")]
    public async Task CheckAsync_LinuxWithMissingOpenSslHashEntry_ReturnsOpenSslCertificateCacheWarning()
    {
        using var certificate = CreateCertificate();
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            CreateCertUtil(tempDirectory);
            CreateOpenSsl(tempDirectory, "12345678");
            var trustDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "trust"));
            File.WriteAllText(Path.Combine(trustDirectory.FullName, GetOpenSslCertificateFileName(certificate)), certificate.ExportCertificatePem());

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
            var check = CreateCheck(toolRunner, environment);

            var results = await check.CheckAsync();

            var cacheResult = Assert.Single(results, r => r.Name == DevCertsCheck.OpenSslCertificateCacheCheckName);
            Assert.Equal(EnvironmentCheckStatus.Warning, cacheResult.Status);
            Assert.Contains(certificate.Thumbprint, cacheResult.Details);
            Assert.Contains("subject-hash", cacheResult.Details);
            Assert.Contains("aspire certs clean", cacheResult.Fix);
            Assert.DoesNotContain("Install openssl", cacheResult.Fix);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "The synthetic openssl command uses a POSIX shell script.")]
    public async Task CheckAsync_LinuxWithCanceledOpenSslHashProbe_ReturnsOpenSslCertificateCacheWarning()
    {
        using var certificate = CreateCertificate();
        var tempDirectory = Directory.CreateTempSubdirectory();
        using var cancellationTokenSource = new CancellationTokenSource();

        try
        {
            CreateCertUtil(tempDirectory);
            CreateOpenSsl(tempDirectory, "12345678");
            var trustDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "trust"));
            File.WriteAllText(Path.Combine(trustDirectory.FullName, GetOpenSslCertificateFileName(certificate)), certificate.ExportCertificatePem());

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
            var processExecutionFactory = new TestProcessExecutionFactory
            {
                AsyncAttemptCallback = async (_, _, cancellationToken) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                    return (0, (string?)"12345678");
                }
            };
            var check = CreateCheck(toolRunner, environment, processExecutionFactory);

            var results = await check.CheckAsync(cancellationTokenSource.Token);

            var cacheResult = Assert.Single(results, r => r.Name == DevCertsCheck.OpenSslCertificateCacheCheckName);
            Assert.Equal(EnvironmentCheckStatus.Warning, cacheResult.Status);
            Assert.Contains(certificate.Thumbprint, cacheResult.Details);
            Assert.Contains("subject-hash", cacheResult.Details);
            var processExecution = Assert.IsType<TestProcessExecution>(Assert.Single(processExecutionFactory.CreatedExecutions));
            Assert.Equal(1, processExecution.KillCount);
            Assert.True(processExecution.KilledEntireProcessTree);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_LinuxWithMissingOpenSslHashEntryAndNoOpenSsl_ReturnsOpenSslCertificateCacheWarningWithOpenSslFix()
    {
        using var certificate = CreateCertificate();
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            CreateCertUtil(tempDirectory);
            var trustDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "trust"));
            File.WriteAllText(Path.Combine(trustDirectory.FullName, GetOpenSslCertificateFileName(certificate)), certificate.ExportCertificatePem());

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
            var check = CreateCheck(toolRunner, environment);

            var results = await check.CheckAsync();

            var cacheResult = Assert.Single(results, r => r.Name == DevCertsCheck.OpenSslCertificateCacheCheckName);
            Assert.Equal(EnvironmentCheckStatus.Warning, cacheResult.Status);
            Assert.Contains(certificate.Thumbprint, cacheResult.Details);
            Assert.Contains("Install openssl", cacheResult.Fix);
            Assert.Contains("aspire certs trust", cacheResult.Fix);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_LinuxWithMissingOpenSslCertificateCacheDirectory_ReturnsOpenSslCertificateCacheWarning()
    {
        using var certificate = CreateCertificate();
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            CreateCertUtil(tempDirectory);
            var trustDirectory = Path.Combine(tempDirectory.FullName, "missing-trust");

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
                [CertificateHelpers.DevCertsOpenSslCertDirEnvVar] = trustDirectory
            });
            var check = CreateCheck(toolRunner, environment);

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
    public async Task CheckAsync_LinuxWithMissingOpenSslCertificateCacheEntry_ReturnsOpenSslCertificateCacheWarning()
    {
        using var certificate = CreateCertificate();
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            CreateCertUtil(tempDirectory);
            var trustDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "trust"));

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
            var check = CreateCheck(toolRunner, environment);

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
    public async Task CheckAsync_LinuxWithCorruptOpenSslCertificateCache_ReturnsOpenSslCertificateCacheWarning()
    {
        using var certificate = CreateCertificate();
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            CreateCertUtil(tempDirectory);
            var trustDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "trust"));
            var corruptCertificateFileName = GetOpenSslCertificateFileName(certificate);
            File.WriteAllText(Path.Combine(trustDirectory.FullName, corruptCertificateFileName), "not a certificate");

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
            var check = CreateCheck(toolRunner, environment);

            var results = await check.CheckAsync();

            var cacheResult = Assert.Single(results, r => r.Name == DevCertsCheck.OpenSslCertificateCacheCheckName);
            Assert.Equal(EnvironmentCheckStatus.Warning, cacheResult.Status);
            Assert.Contains(corruptCertificateFileName, cacheResult.Details);
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
            var corruptCertificateFileName = GetOpenSslCertificateFileName(certificate);
            File.WriteAllText(Path.Combine(trustDirectory.FullName, corruptCertificateFileName), "not a certificate");

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
            var check = CreateCheck(toolRunner, environment);

            var results = await check.CheckAsync();

            var cacheResult = Assert.Single(results, r => r.Name == DevCertsCheck.OpenSslCertificateCacheCheckName);
            Assert.Equal(EnvironmentCheckStatus.Warning, cacheResult.Status);
            Assert.Contains(corruptCertificateFileName, cacheResult.Details);
            Assert.Contains("aspire certs clean", cacheResult.Fix);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_LinuxWithUnrelatedCorruptOpenSslCertificateCache_DoesNotReturnOpenSslCertificateCacheWarning()
    {
        using var certificate = CreateCertificate();
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            CreateCertUtil(tempDirectory);
            var trustDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "trust"));
            WriteOpenSslCertificateCache(trustDirectory, certificate);
            File.WriteAllText(Path.Combine(trustDirectory.FullName, "unrelated.pem"), "not a certificate");

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
            var check = CreateCheck(toolRunner, environment);

            var results = await check.CheckAsync();

            Assert.DoesNotContain(results, r => r.Name == DevCertsCheck.OpenSslCertificateCacheCheckName);
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
            File.WriteAllText(Path.Combine(trustDirectory.FullName, GetOpenSslCertificateFileName(certificate)), staleCertificate.ExportCertificatePem());

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
            var check = CreateCheck(toolRunner, environment);

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
        var check = CreateCheck(toolRunner, environment);

        var results = await check.CheckAsync();

        Assert.Equal(0, environment.GetEnvironmentVariablesCallCount);
        Assert.Collection(results, result => Assert.Equal(DevCertsCheck.CheckName, result.Name));
    }
}
