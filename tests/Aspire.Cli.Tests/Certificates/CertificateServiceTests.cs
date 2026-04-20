// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Certificates;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Certificates;

public class CertificateServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task EnsureCertificatesTrustedAsync_WithFullyTrustedCert_StillRunsTrustToUpdateAspireCache()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var trustCalled = false;

        using var sp = CreateServiceProvider(workspace, new TestCertificateToolRunner
        {
            TrustHttpCertificateCallback = () =>
            {
                trustCalled = true;
                return EnsureCertificateResult.ExistingHttpsCertificateTrusted;
            },
            CheckHttpCertificateCallback = () => CreateTrustResult(CertificateManager.TrustLevel.Full)
        });

        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(trustCalled);
        Assert.True(result.Success);
        Assert.False(result.WasCancelled);
        Assert.Equal(EnsureCertificateResult.ExistingHttpsCertificateTrusted, result.ResultCode);
        Assert.Empty(result.EnvironmentVariables);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_WithNoCertificates_RunsTrustOperation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var trustCalled = false;

        using var sp = CreateServiceProvider(workspace, new TestCertificateToolRunner
        {
            TrustHttpCertificateCallback = () =>
            {
                trustCalled = true;
                return EnsureCertificateResult.NewHttpsCertificateTrusted;
            },
            CheckHttpCertificateCallback = () => CreateTrustResult(CertificateManager.TrustLevel.Full)
        });

        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(trustCalled);
        Assert.True(result.Success);
        Assert.False(result.WasCancelled);
        Assert.Equal(EnsureCertificateResult.NewHttpsCertificateTrusted, result.ResultCode);
        Assert.Empty(result.EnvironmentVariables);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_WithPartiallyTrustedCert_RunsTrustAndSetsSslCertDirOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var trustCalled = false;

        using var sp = CreateServiceProvider(workspace, new TestCertificateToolRunner
        {
            TrustHttpCertificateCallback = () =>
            {
                trustCalled = true;
                return EnsureCertificateResult.PartiallyFailedToTrustTheCertificate;
            },
            CheckHttpCertificateCallback = () => CreateTrustResult(CertificateManager.TrustLevel.Partial)
        });

        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(trustCalled);
        Assert.False(result.Success);
        Assert.False(result.WasCancelled);
        Assert.Equal(EnsureCertificateResult.PartiallyFailedToTrustTheCertificate, result.ResultCode);
        Assert.True(result.EnvironmentVariables.ContainsKey("SSL_CERT_DIR"));
        Assert.Contains(".aspnet/dev-certs/trust", result.EnvironmentVariables["SSL_CERT_DIR"]);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_TrustOperationFails_ReturnsFailure()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        using var sp = CreateServiceProvider(workspace, new TestCertificateToolRunner
        {
            TrustHttpCertificateCallback = () => EnsureCertificateResult.FailedToTrustTheCertificate,
            CheckHttpCertificateCallback = () => CreateTrustResult(CertificateManager.TrustLevel.None)
        });

        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.False(result.Success);
        Assert.False(result.WasCancelled);
        Assert.Equal(EnsureCertificateResult.FailedToTrustTheCertificate, result.ResultCode);
        Assert.Empty(result.EnvironmentVariables);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_TrustOperationCancelled_ReturnsWasCancelled()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        using var sp = CreateServiceProvider(workspace, new TestCertificateToolRunner
        {
            TrustHttpCertificateCallback = () => EnsureCertificateResult.UserCancelledTrustStep,
            CheckHttpCertificateCallback = () => CreateTrustResult(CertificateManager.TrustLevel.None)
        });

        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.False(result.Success);
        Assert.True(result.WasCancelled);
        Assert.Equal(EnsureCertificateResult.UserCancelledTrustStep, result.ResultCode);
        Assert.Empty(result.EnvironmentVariables);
    }

    private ServiceProvider CreateServiceProvider(TemporaryWorkspace workspace, TestCertificateToolRunner toolRunner)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = _ => toolRunner;
        });

        return services.BuildServiceProvider();
    }

    private static CertificateTrustResult CreateTrustResult(CertificateManager.TrustLevel? trustLevel)
    {
        if (trustLevel is null)
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
            TrustLevel = trustLevel,
            Certificates =
            [
                new DevCertInfo
                {
                    Version = 5,
                    TrustLevel = trustLevel.Value,
                    IsHttpsDevelopmentCertificate = true,
                    ValidityNotBefore = DateTimeOffset.Now.AddDays(-1),
                    ValidityNotAfter = DateTimeOffset.Now.AddDays(365)
                }
            ]
        };
    }
}
