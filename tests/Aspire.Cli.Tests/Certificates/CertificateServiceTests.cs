// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Cli.Certificates;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Certificates;

public class CertificateServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task EnsureCertificatesTrustedAsync_WithFullyTrustedCert_ReturnsEmptyEnvVars()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = sp =>
            {
                return new TestCertificateToolRunner
                {
                    CheckHttpCertificateCallback = () =>
                    {
                        return new CertificateTrustResult
                        {
                            HasCertificates = true,
                            TrustLevel = CertificateManager.TrustLevel.Full,
                            Certificates = [new DevCertInfo { Version = 5, TrustLevel = CertificateManager.TrustLevel.Full, IsHttpsDevelopmentCertificate = true, ValidityNotBefore = DateTimeOffset.Now.AddDays(-1), ValidityNotAfter = DateTimeOffset.Now.AddDays(365) }]
                        };
                    }
                };
            };
        });

        var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.NotNull(result);
        Assert.Empty(result.EnvironmentVariables);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_WithNotTrustedCert_RunsTrustOperation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var trustCalled = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = sp =>
            {
                var callCount = 0;
                return new TestCertificateToolRunner
                {
                    CheckHttpCertificateCallback = () =>
                    {
                        callCount++;
                        // First call returns not trusted, second call (after trust) returns fully trusted
                        if (callCount == 1)
                        {
                            return new CertificateTrustResult
                            {
                                HasCertificates = true,
                                TrustLevel = CertificateManager.TrustLevel.None,
                                Certificates = [new DevCertInfo { Version = 5, TrustLevel = CertificateManager.TrustLevel.None, IsHttpsDevelopmentCertificate = true, ValidityNotBefore = DateTimeOffset.Now.AddDays(-1), ValidityNotAfter = DateTimeOffset.Now.AddDays(365) }]
                            };
                        }
                        return new CertificateTrustResult
                        {
                            HasCertificates = true,
                            TrustLevel = CertificateManager.TrustLevel.Full,
                            Certificates = [new DevCertInfo { Version = 5, TrustLevel = CertificateManager.TrustLevel.Full, IsHttpsDevelopmentCertificate = true, ValidityNotBefore = DateTimeOffset.Now.AddDays(-1), ValidityNotAfter = DateTimeOffset.Now.AddDays(365) }]
                        };
                    },
                    TrustHttpCertificateCallback = () =>
                    {
                        trustCalled = true;
                        return EnsureCertificateResult.ExistingHttpsCertificateTrusted;
                    }
                };
            };
        });

        var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(trustCalled);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_WithPartiallyTrustedCert_SetsSslCertDirOnLinux()
    {
        // Skip this test on non-Linux platforms
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = sp =>
            {
                return new TestCertificateToolRunner
                {
                    CheckHttpCertificateCallback = () =>
                    {
                        return new CertificateTrustResult
                        {
                            HasCertificates = true,
                            TrustLevel = CertificateManager.TrustLevel.Partial,
                            Certificates = [new DevCertInfo { Version = 5, TrustLevel = CertificateManager.TrustLevel.Partial, IsHttpsDevelopmentCertificate = true, ValidityNotBefore = DateTimeOffset.Now.AddDays(-1), ValidityNotAfter = DateTimeOffset.Now.AddDays(365) }]
                        };
                    }
                };
            };
        });

        var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.NotNull(result);
        Assert.True(result.EnvironmentVariables.ContainsKey("SSL_CERT_DIR"));
        Assert.Contains(".aspnet/dev-certs/trust", result.EnvironmentVariables["SSL_CERT_DIR"]);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_WithNoCertificates_RunsTrustOperation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var trustCalled = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = sp =>
            {
                var callCount = 0;
                return new TestCertificateToolRunner
                {
                    CheckHttpCertificateCallback = () =>
                    {
                        callCount++;
                        // First call returns no certificates, second call (after trust) returns fully trusted
                        if (callCount == 1)
                        {
                            return new CertificateTrustResult
                            {
                                HasCertificates = false,
                                TrustLevel = null,
                                Certificates = []
                            };
                        }
                        return new CertificateTrustResult
                        {
                            HasCertificates = true,
                            TrustLevel = CertificateManager.TrustLevel.Full,
                            Certificates = [new DevCertInfo { Version = 5, TrustLevel = CertificateManager.TrustLevel.Full, IsHttpsDevelopmentCertificate = true, ValidityNotBefore = DateTimeOffset.Now.AddDays(-1), ValidityNotAfter = DateTimeOffset.Now.AddDays(365) }]
                        };
                    },
                    TrustHttpCertificateCallback = () =>
                    {
                        trustCalled = true;
                        return EnsureCertificateResult.NewHttpsCertificateTrusted;
                    }
                };
            };
        });

        var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(trustCalled);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_TrustOperationFails_DisplaysWarning()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = sp =>
            {
                return new TestCertificateToolRunner
                {
                    CheckHttpCertificateCallback = () =>
                    {
                        return new CertificateTrustResult
                        {
                            HasCertificates = true,
                            TrustLevel = CertificateManager.TrustLevel.None,
                            Certificates = [new DevCertInfo { Version = 5, TrustLevel = CertificateManager.TrustLevel.None, IsHttpsDevelopmentCertificate = true, ValidityNotBefore = DateTimeOffset.Now.AddDays(-1), ValidityNotAfter = DateTimeOffset.Now.AddDays(365) }]
                        };
                    },
                    TrustHttpCertificateCallback = () =>
                    {
                        return EnsureCertificateResult.FailedToTrustTheCertificate;
                    }
                };
            };
        });

        var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();

        // If this does not throw then the code is behaving correctly.
        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.NotNull(result);
    }

    private sealed class TestCertificateToolRunner : ICertificateToolRunner
    {
        public Func<CertificateTrustResult>? CheckHttpCertificateCallback { get; set; }
        public Func<EnsureCertificateResult>? TrustHttpCertificateCallback { get; set; }

        public CertificateTrustResult CheckHttpCertificate()
        {
            if (CheckHttpCertificateCallback is not null)
            {
                return CheckHttpCertificateCallback();
            }

            // Default: Return a fully trusted certificate result
            return new CertificateTrustResult
            {
                HasCertificates = true,
                TrustLevel = CertificateManager.TrustLevel.Full,
                Certificates = []
            };
        }

        public EnsureCertificateResult TrustHttpCertificate()
        {
            return TrustHttpCertificateCallback is not null
                ? TrustHttpCertificateCallback()
                : EnsureCertificateResult.ExistingHttpsCertificateTrusted;
        }

        public CertificateCleanResult CleanHttpCertificate()
            => new CertificateCleanResult { Success = true };
    }
}
