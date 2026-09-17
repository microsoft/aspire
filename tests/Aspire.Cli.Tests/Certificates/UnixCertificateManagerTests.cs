// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Certificates;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using System.Diagnostics;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Certificates;

public class UnixCertificateManagerTests
{
    [Fact]
    public void ResolveNssDbs_DiscoversFirefoxProfileFromDefaultXdgConfigDirectory()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var profileDirectory = CreateDirectory(homeDirectory, ".config", "mozilla", "firefox", "test.default-release");
            var manager = CreateManager();

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(profileDirectory, nssDb.Path);
            AssertFirefoxNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_DiscoversFirefoxProfileFromConfiguredXdgConfigDirectory()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var xdgConfigHome = CreateDirectory(homeDirectory, "custom-config");
            var profileDirectory = CreateDirectory(homeDirectory, "custom-config", "mozilla", "firefox", "test.default-release");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                ["XDG_CONFIG_HOME"] = xdgConfigHome
            });

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(profileDirectory, nssDb.Path);
            AssertFirefoxNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_IgnoresRelativeXdgConfigDirectory()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var profileDirectory = CreateDirectory(homeDirectory, ".config", "mozilla", "firefox", "test.default-release");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                ["XDG_CONFIG_HOME"] = "relative-config"
            });

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(profileDirectory, nssDb.Path);
            AssertFirefoxNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_PrefersLegacyFirefoxDirectoryWhenPresent()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var legacyProfileDirectory = CreateDirectory(homeDirectory, ".mozilla", "firefox", "legacy.default-release");
            CreateDirectory(homeDirectory, ".config", "mozilla", "firefox", "xdg.default-release");
            var manager = CreateManager();

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(legacyProfileDirectory, nssDb.Path);
            AssertFirefoxNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_RecognizesStandardXdgFirefoxOverride()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var profileDirectory = CreateDirectory(homeDirectory, ".config", "mozilla", "firefox", "test.default-release");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = profileDirectory
            });

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(profileDirectory, nssDb.Path);
            AssertFirefoxNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_RecognizesConfiguredXdgFirefoxOverride()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var xdgConfigHome = CreateDirectory(homeDirectory, "custom-config");
            var profileDirectory = CreateDirectory(homeDirectory, "custom-config", "mozilla", "firefox", "test.default-release");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                ["XDG_CONFIG_HOME"] = xdgConfigHome,
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = profileDirectory
            });

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(profileDirectory, nssDb.Path);
            AssertFirefoxNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_RecognizesTypedFirefoxOverride()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var profileDirectory = CreateDirectory(homeDirectory, "custom-browser", "profile");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = $"firefox={profileDirectory}"
            });

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(profileDirectory, nssDb.Path);
            AssertFirefoxNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_RecognizesAspireOverride()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var profileDirectory = CreateDirectory(homeDirectory, "custom-browser", "profile");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                [KnownConfigNames.CliDevCertsNssDbPaths] = $"firefox={profileDirectory}"
            });

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(profileDirectory, nssDb.Path);
            AssertFirefoxNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_AspireOverrideTakesPrecedence()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var aspireProfileDirectory = CreateDirectory(homeDirectory, "aspire-browser", "profile");
            var dotnetProfileDirectory = CreateDirectory(homeDirectory, "dotnet-browser", "profile");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                [KnownConfigNames.CliDevCertsNssDbPaths] = $"firefox={aspireProfileDirectory}",
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = $"chromium={dotnetProfileDirectory}"
            });

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(aspireProfileDirectory, nssDb.Path);
            AssertFirefoxNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_EmptyAspireOverrideFallsBackToDotnetOverride()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var dotnetProfileDirectory = CreateDirectory(homeDirectory, "dotnet-browser", "profile");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                [KnownConfigNames.CliDevCertsNssDbPaths] = string.Empty,
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = $"chromium={dotnetProfileDirectory}"
            });

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(dotnetProfileDirectory, nssDb.Path);
            AssertChromiumNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_AspireOverrideNamesAspireVariableInDiagnostics()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var missingProfileDirectory = Path.Combine(homeDirectory.FullName, "missing-profile");
            var sink = new TestSink();
            var logger = new TestLogger(nameof(UnixCertificateManager), sink, enabled: true);
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                [KnownConfigNames.CliDevCertsNssDbPaths] = missingProfileDirectory
            });
            var manager = new UnixCertificateManager(logger, environment);

            var nssDbs = manager.GetNssDbs(homeDirectory.FullName);

            Assert.Empty(nssDbs);
            Assert.Collection(
                sink.Writes,
                write => Assert.Equal(
                    $"Reading NSS database locations from {KnownConfigNames.CliDevCertsNssDbPaths}.",
                    write.Message),
                write => Assert.Equal(
                    $"The NSS database '{missingProfileDirectory}' provided via {KnownConfigNames.CliDevCertsNssDbPaths} does not exist.",
                    write.Message));
        });
    }

    [Fact]
    public void ResolveNssDbs_RecognizesTypedChromiumOverride()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var profileDirectory = CreateDirectory(homeDirectory, ".mozilla", "firefox", "test.default-release");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = $"chromium={profileDirectory}"
            });

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(profileDirectory, nssDb.Path);
            AssertChromiumNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_PreservesUntypedChromiumOverride()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var profileDirectory = CreateDirectory(homeDirectory, "custom-browser", "profile");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = profileDirectory
            });

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(profileDirectory, nssDb.Path);
            AssertChromiumNssDb(nssDb);
        });
    }

    [Fact]
    public void ResolveNssDbs_IgnoresEmptyTypedOverrides()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var manager = CreateManager(new Dictionary<string, string?>
            {
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = $"firefox={Path.PathSeparator}chromium="
            });

            var nssDbs = manager.GetNssDbs(homeDirectory.FullName);

            Assert.Empty(nssDbs);
        });
    }

    [Fact]
    public void ResolveNssDbs_OverridesReplaceDiscovery()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            CreateDirectory(homeDirectory, ".pki", "nssdb");
            var overrideDirectory = CreateDirectory(homeDirectory, "custom-browser", "profile");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = overrideDirectory
            });

            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));

            Assert.Equal(overrideDirectory, nssDb.Path);
        });
    }

    [Fact]
    public void CreateProcessStartInfos_PreserveFirefoxArgumentsContainingSpaces()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var profileDirectory = CreateDirectory(homeDirectory, "custom browser", "profile with spaces");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = $"firefox={profileDirectory}"
            });
            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));
            var certificatePath = Path.Combine(homeDirectory.FullName, "certificate with spaces.pem");
            const string nickname = "nickname with spaces";

            AssertProcessStartInfo(
                nssDb.CreateCheckProcessStartInfo(nickname),
                "-d", $"sql:{profileDirectory}", "-n", nickname, "-L");
            AssertProcessStartInfo(
                nssDb.CreateAddProcessStartInfo(certificatePath, nickname),
                "-d", $"sql:{profileDirectory}", "-n", nickname, "-A", "-i", certificatePath, "-t", "C,,");
            AssertProcessStartInfo(
                nssDb.CreateRemoveProcessStartInfo(nickname),
                "-d", $"sql:{profileDirectory}", "-n", nickname, "-D");
        });
    }

    [Fact]
    public void CreateProcessStartInfos_PreserveChromiumArgumentsContainingSpaces()
    {
        RunWithTemporaryHome(homeDirectory =>
        {
            var profileDirectory = CreateDirectory(homeDirectory, "custom browser", "profile with spaces");
            var manager = CreateManager(new Dictionary<string, string?>
            {
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = $"chromium={profileDirectory}"
            });
            var nssDb = Assert.Single(manager.GetNssDbs(homeDirectory.FullName));
            var certificatePath = Path.Combine(homeDirectory.FullName, "certificate with spaces.pem");
            const string nickname = "nickname with spaces";

            AssertProcessStartInfo(
                nssDb.CreateCheckProcessStartInfo(nickname),
                "-d", $"sql:{profileDirectory}", "-n", nickname, "-V", "-u", "V");
            AssertProcessStartInfo(
                nssDb.CreateAddProcessStartInfo(certificatePath, nickname),
                "-d", $"sql:{profileDirectory}", "-n", nickname, "-A", "-i", certificatePath, "-t", "P,,");
            AssertProcessStartInfo(
                nssDb.CreateRemoveProcessStartInfo(nickname),
                "-d", $"sql:{profileDirectory}", "-n", nickname, "-D");
        });
    }

    [Fact]
    public async Task GetTrustLevel_WhenCanceled_KillsCertUtilProcessTree()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "NSS certificate trust is only exercised on Linux.");

        var tempDirectory = Directory.CreateTempSubdirectory();
        var nssDbDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "nssdb"));
        var parentPidFile = Path.Combine(tempDirectory.FullName, "certutil-parent.pid");
        var childPidFile = Path.Combine(tempDirectory.FullName, "certutil-child.pid");
        var parentPid = 0;
        var childPid = 0;
        using var cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var certUtilFile = await CreateBlockingCertUtilAsync(tempDirectory, parentPidFile, childPidFile);
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = tempDirectory.FullName,
                ["SSL_CERT_DIR"] = tempDirectory.FullName,
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = nssDbDirectory.FullName
            });
            var manager = new UnixCertificateManager(NullLogger.Instance, environment, startInfo =>
            {
                startInfo.FileName = certUtilFile.FullName;
            });
            using var certificate = manager.CreateAspNetCoreHttpsDevelopmentCertificate(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));
            var trustTask = Task.Run(() => manager.GetTrustLevel(certificate, cancellationTokenSource.Token));

            parentPid = await ProcessTestHelpers.WaitForProcessIdAsync(parentPidFile, TestContext.Current.CancellationToken)
                .DefaultTimeout();
            childPid = await ProcessTestHelpers.WaitForProcessIdAsync(childPidFile, TestContext.Current.CancellationToken)
                .DefaultTimeout();

            cancellationTokenSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trustTask).DefaultTimeout();
            Assert.True(ProcessTestHelpers.WaitForProcessExit(parentPid, TimeSpan.FromSeconds(10)), $"Expected certutil process {parentPid} to exit.");
            Assert.True(ProcessTestHelpers.WaitForProcessExit(childPid, TimeSpan.FromSeconds(10)), $"Expected child process {childPid} to exit.");
        }
        finally
        {
            cancellationTokenSource.Cancel();
            ProcessTestHelpers.TryKillProcess(parentPid);
            ProcessTestHelpers.TryKillProcess(childPid);
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetTrustLevel_WhenCertUtilFillsOutputPipes_Completes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "NSS certificate trust is only exercised on Linux.");

        var tempDirectory = Directory.CreateTempSubdirectory();
        var nssDbDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "nssdb"));

        try
        {
            var certUtilFile = await CreateNoisyCertUtilAsync(tempDirectory);
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = tempDirectory.FullName,
                ["SSL_CERT_DIR"] = tempDirectory.FullName,
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = nssDbDirectory.FullName
            });
            var manager = new UnixCertificateManager(NullLogger.Instance, environment, startInfo =>
            {
                startInfo.FileName = certUtilFile.FullName;
            });
            using var certificate = manager.CreateAspNetCoreHttpsDevelopmentCertificate(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));

            var trustLevel = await Task.Run(() => manager.GetTrustLevel(certificate))
                .DefaultTimeout();

            Assert.Equal(CertificateManager.TrustLevel.None, trustLevel);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetTrustLevel_WithCorruptOpenSslCertificate_DoesNotThrow()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "OpenSSL certificate directory trust is only exercised on Linux.");

        var openSslDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = Path.Combine(openSslDirectory.FullName, "missing-tools"),
                ["SSL_CERT_DIR"] = openSslDirectory.FullName,
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = Path.Combine(openSslDirectory.FullName, "missing-nss-db")
            });
            var manager = new UnixCertificateManager(NullLogger.Instance, environment);
            using var certificate = manager.CreateAspNetCoreHttpsDevelopmentCertificate(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));
            var certificatePath = Path.Combine(openSslDirectory.FullName, $"aspnetcore-localhost-{certificate.Thumbprint}.pem");
            File.WriteAllText(certificatePath, "not a certificate");

            var trustLevel = manager.GetTrustLevel(certificate);

            Assert.Equal(CertificateManager.TrustLevel.None, trustLevel);
        }
        finally
        {
            openSslDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetTrustLevel_WithCorruptOpenSslCertificateBeforeValidCertificate_DoesNotLogOpenSslWarning()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "OpenSSL certificate directory trust is only exercised on Linux.");

        var corruptOpenSslDirectory = Directory.CreateTempSubdirectory();
        var validOpenSslDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var sink = new TestSink();
            var logger = new TestLogger(nameof(UnixCertificateManager), sink, enabled: true);
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = Path.Combine(corruptOpenSslDirectory.FullName, "missing-tools"),
                ["SSL_CERT_DIR"] = string.Join(Path.PathSeparator, corruptOpenSslDirectory.FullName, validOpenSslDirectory.FullName),
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = Path.Combine(corruptOpenSslDirectory.FullName, "missing-nss-db")
            });
            var manager = new UnixCertificateManager(logger, environment);
            using var certificate = manager.CreateAspNetCoreHttpsDevelopmentCertificate(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));
            var certificateFileName = $"aspnetcore-localhost-{certificate.Thumbprint}.pem";
            File.WriteAllText(Path.Combine(corruptOpenSslDirectory.FullName, certificateFileName), "not a certificate");
            File.WriteAllText(Path.Combine(validOpenSslDirectory.FullName, certificateFileName), certificate.ExportCertificatePem());

            var trustLevel = manager.GetTrustLevel(certificate);

            Assert.NotEqual(CertificateManager.TrustLevel.None, trustLevel);
            Assert.DoesNotContain(sink.Writes, w => w.Message?.Contains("not trusted by OpenSSL", StringComparison.Ordinal) == true);
        }
        finally
        {
            corruptOpenSslDirectory.Delete(recursive: true);
            validOpenSslDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RemoveCertificate_WithMissingOpenSsl_DeletesOpenSslCertificate()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "OpenSSL certificate cleanup is only exercised on Linux.");

        var openSslDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = Path.Combine(openSslDirectory.FullName, "missing-tools"),
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = Path.Combine(openSslDirectory.FullName, "missing-nss-db"),
                [CertificateHelpers.DevCertsOpenSslCertDirEnvVar] = openSslDirectory.FullName
            });
            var manager = new UnixCertificateManager(NullLogger.Instance, environment);
            using var certificate = manager.CreateAspNetCoreHttpsDevelopmentCertificate(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));
            using var savedCertificate = manager.SaveCertificate(certificate);
            var certificatePath = Path.Combine(openSslDirectory.FullName, $"aspnetcore-localhost-{savedCertificate.Thumbprint}.pem");
            File.WriteAllText(certificatePath, "not a certificate");

            var exception = Record.Exception(() => manager.RemoveCertificate(savedCertificate, CertificateManager.RemoveLocations.All));

            Assert.Null(exception);
            Assert.False(File.Exists(certificatePath));
        }
        finally
        {
            openSslDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RemoveCertificate_WithMissingCertUtilAndNssDbs_SkipsNssCleanup()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "NSS certificate cleanup is only exercised on Linux.");

        var nssDbDirectory = Directory.CreateTempSubdirectory();
        var openSslDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = Path.Combine(nssDbDirectory.FullName, "missing-certutil"),
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = nssDbDirectory.FullName,
                [CertificateHelpers.DevCertsOpenSslCertDirEnvVar] = openSslDirectory.FullName
            });
            var manager = new UnixCertificateManager(NullLogger.Instance, environment);
            using var certificate = manager.CreateAspNetCoreHttpsDevelopmentCertificate(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));

            var exception = Record.Exception(() => manager.RemoveCertificate(certificate, CertificateManager.RemoveLocations.Trusted));

            Assert.Null(exception);
        }
        finally
        {
            nssDbDirectory.Delete(recursive: true);
            openSslDirectory.Delete(recursive: true);
        }
    }

    private static async Task<FileInfo> CreateBlockingCertUtilAsync(DirectoryInfo directory, string parentPidFile, string childPidFile)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var certUtilFile = new FileInfo(Path.Combine(directory.FullName, "certutil"));
        var script =
            "#!/usr/bin/env bash" + Environment.NewLine +
            $"echo $$ > '{EscapeShellPath(parentPidFile)}'" + Environment.NewLine +
            "sleep 60 &" + Environment.NewLine +
            $"echo $! > '{EscapeShellPath(childPidFile)}'" + Environment.NewLine +
            "wait $!" + Environment.NewLine;
        await File.WriteAllTextAsync(certUtilFile.FullName, script);
        File.SetUnixFileMode(
            certUtilFile.FullName,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        return certUtilFile;
    }

    private static async Task<FileInfo> CreateNoisyCertUtilAsync(DirectoryInfo directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var certUtilFile = new FileInfo(Path.Combine(directory.FullName, "certutil"));
        var script =
            "#!/usr/bin/env bash" + Environment.NewLine +
            "for _ in {1..16}; do" + Environment.NewLine +
            "  printf '%65536s' x" + Environment.NewLine +
            "  printf '%65536s' x >&2" + Environment.NewLine +
            "done" + Environment.NewLine +
            "exit 1" + Environment.NewLine;
        await File.WriteAllTextAsync(certUtilFile.FullName, script);
        File.SetUnixFileMode(
            certUtilFile.FullName,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        return certUtilFile;
    }

    private static UnixCertificateManager CreateManager(IReadOnlyDictionary<string, string?>? variables = null)
        => new(NullLogger.Instance, TestEnvironment.CreateLinux(variables));

    private static string CreateDirectory(DirectoryInfo homeDirectory, params string[] pathSegments)
    {
        var path = Path.Combine([homeDirectory.FullName, .. pathSegments]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RunWithTemporaryHome(Action<DirectoryInfo> test)
    {
        var homeDirectory = Directory.CreateTempSubdirectory();
        try
        {
            test(homeDirectory);
        }
        finally
        {
            homeDirectory.Delete(recursive: true);
        }
    }

    private static void AssertFirefoxNssDb(UnixCertificateManager.NssDb nssDb)
    {
        Assert.Equal("Firefox", nssDb.BrowserFamily);
        Assert.Equal(["-L"], nssDb.CheckArguments);
        Assert.Equal("C", nssDb.TrustUsage);
    }

    private static void AssertChromiumNssDb(UnixCertificateManager.NssDb nssDb)
    {
        Assert.Equal("Chromium", nssDb.BrowserFamily);
        Assert.Equal(["-V", "-u", "V"], nssDb.CheckArguments);
        Assert.Equal("P", nssDb.TrustUsage);
    }

    private static void AssertProcessStartInfo(ProcessStartInfo startInfo, params string[] expectedArguments)
    {
        Assert.Equal(CertificateHelpers.CertUtilCommand, startInfo.FileName);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(expectedArguments, startInfo.ArgumentList);
    }

    private static string EscapeShellPath(string path) => path.Replace("'", "'\"'\"'", StringComparison.Ordinal);
}
