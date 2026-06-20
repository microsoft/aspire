// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Certificates;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
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
    public async Task EnsureCertificatesTrustedAsync_WithPartiallyTrustedCert_ReturnsFailureForInteractiveLinux()
    {
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
        }, nonInteractive: false, environment: new TestEnvironment { IsLinux = true });

        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(trustCalled);
        Assert.False(result.Success);
        Assert.False(result.WasCancelled);
        Assert.Equal(EnsureCertificateResult.PartiallyFailedToTrustTheCertificate, result.ResultCode);
        AssertSslCertDirContainsDevCertsTrustPath(result.EnvironmentVariables);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_WithPartiallyTrustedCert_ReturnsSuccessForNonInteractiveLinux()
    {
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
        }, nonInteractive: true, environment: new TestEnvironment { IsLinux = true });

        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(trustCalled);
        Assert.True(result.Success);
        Assert.False(result.WasCancelled);
        Assert.Equal(EnsureCertificateResult.PartiallyFailedToTrustTheCertificate, result.ResultCode);
        AssertSslCertDirContainsDevCertsTrustPath(result.EnvironmentVariables);
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

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_NonInteractive_ChecksButDoesNotTrustOnNonLinux()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Non-interactive skip only applies to macOS/Windows; Linux trust is non-interactive.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var trustCalled = false;
        var checkCalled = false;

        var toolRunner = new TestCertificateToolRunner
        {
            TrustHttpCertificateCallback = () =>
            {
                trustCalled = true;
                return EnsureCertificateResult.ExistingHttpsCertificateTrusted;
            },
            CheckHttpCertificateCallback = () =>
            {
                checkCalled = true;
                return CreateTrustResult(CertificateManager.TrustLevel.Full);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = _ => toolRunner;
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
        });

        using var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.False(trustCalled);
        Assert.True(checkCalled);
        Assert.True(result.Success);
        Assert.False(result.WasCancelled);
        Assert.Null(result.ResultCode);
        Assert.Empty(result.EnvironmentVariables);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_NonInteractive_WarnsWhenUntrustedOnNonLinux()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Non-interactive skip only applies to macOS/Windows; Linux trust is non-interactive.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var trustCalled = false;

        var toolRunner = new TestCertificateToolRunner
        {
            TrustHttpCertificateCallback = () =>
            {
                trustCalled = true;
                return EnsureCertificateResult.ExistingHttpsCertificateTrusted;
            },
            CheckHttpCertificateCallback = () => CreateTrustResult(CertificateManager.TrustLevel.None)
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = _ => toolRunner;
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
            options.InteractionServiceFactory = _ => new TestInteractionService();
        });

        using var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();
        var interactionService = (TestInteractionService)sp.GetRequiredService<IInteractionService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.False(trustCalled);
        Assert.True(result.Success);
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message.Contains("are not trusted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_NonInteractive_WarnsWhenPartiallyTrustedOnNonLinux()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Non-interactive skip only applies to macOS/Windows; Linux trust is non-interactive.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var trustCalled = false;

        var toolRunner = new TestCertificateToolRunner
        {
            TrustHttpCertificateCallback = () =>
            {
                trustCalled = true;
                return EnsureCertificateResult.ExistingHttpsCertificateTrusted;
            },
            CheckHttpCertificateCallback = () => CreateTrustResult(CertificateManager.TrustLevel.Partial)
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = _ => toolRunner;
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
            options.InteractionServiceFactory = _ => new TestInteractionService();
        });

        using var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();
        var interactionService = (TestInteractionService)sp.GetRequiredService<IInteractionService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.False(trustCalled);
        Assert.True(result.Success);
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message.Contains("partially trusted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_NonInteractive_ProceedsOnLinux()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var trustCalled = false;

        var toolRunner = new TestCertificateToolRunner
        {
            TrustHttpCertificateCallback = () =>
            {
                trustCalled = true;
                return EnsureCertificateResult.ExistingHttpsCertificateTrusted;
            },
            CheckHttpCertificateCallback = () => CreateTrustResult(CertificateManager.TrustLevel.Full)
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = _ => toolRunner;
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
            options.CertificateServiceFactory = sp =>
            {
                var interactiveService = sp.GetRequiredService<IInteractionService>();
                var telemetry = sp.GetRequiredService<AspireCliTelemetry>();
                var hostEnvironment = sp.GetRequiredService<ICliHostEnvironment>();
                var executionContext = sp.GetRequiredService<CliExecutionContext>();
                return new CertificateService(toolRunner, interactiveService, telemetry, hostEnvironment, executionContext, new TestEnvironment { IsLinux = true });
            };
        });

        using var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(trustCalled);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_NonInteractive_GeneratesCertWhenNoneExist()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Non-interactive cert generation test only applies to macOS/Windows.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var generateCalled = false;
        var checkCallCount = 0;

        var toolRunner = new TestCertificateToolRunner
        {
            CheckHttpCertificateCallback = () =>
            {
                checkCallCount++;
                // First call: no certs exist. Second call (after generation): untrusted cert present.
                return checkCallCount == 1 ? CreateNoCertsResult() : CreateTrustResult(CertificateManager.TrustLevel.None);
            },
            EnsureHttpCertificateExistsCallback = () =>
            {
                generateCalled = true;
                return EnsureCertificateResult.Succeeded;
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = _ => toolRunner;
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
            options.InteractionServiceFactory = _ => new TestInteractionService();
        });

        using var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(generateCalled);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_NonInteractive_SkipsCertGenerationWhenCertsExist()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Non-interactive cert generation test only applies to macOS/Windows.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var generateCalled = false;

        var toolRunner = new TestCertificateToolRunner
        {
            CheckHttpCertificateCallback = () => CreateTrustResult(CertificateManager.TrustLevel.Full),
            EnsureHttpCertificateExistsCallback = () =>
            {
                generateCalled = true;
                return EnsureCertificateResult.Succeeded;
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = _ => toolRunner;
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
        });

        using var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.False(generateCalled);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_NonInteractive_EnvVarOptOutSuppressesCertGeneration()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Non-interactive cert generation test only applies to macOS/Windows.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var generateCalled = false;

        var toolRunner = new TestCertificateToolRunner
        {
            CheckHttpCertificateCallback = () => CreateNoCertsResult(),
            EnsureHttpCertificateExistsCallback = () =>
            {
                generateCalled = true;
                return EnsureCertificateResult.Succeeded;
            }
        };

        var envVars = new Dictionary<string, string?>
        {
            [KnownConfigNames.CliGenerateHttpsCertificate] = "false"
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = _ => toolRunner;
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(
                workspace.WorkspaceRoot, environment: new TestEnvironment(envVars));
            options.InteractionServiceFactory = _ => new TestInteractionService();
        });

        using var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.False(generateCalled);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task EnsureCertificatesTrustedAsync_NonInteractive_WarnsOnCertGenerationFailure()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Non-interactive cert generation test only applies to macOS/Windows.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var toolRunner = new TestCertificateToolRunner
        {
            CheckHttpCertificateCallback = () => CreateNoCertsResult(),
            EnsureHttpCertificateExistsCallback = () => EnsureCertificateResult.ErrorCreatingTheCertificate
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = _ => toolRunner;
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
            options.InteractionServiceFactory = _ => new TestInteractionService();
        });

        using var sp = services.BuildServiceProvider();
        var cs = sp.GetRequiredService<ICertificateService>();
        var interactionService = (TestInteractionService)sp.GetRequiredService<IInteractionService>();

        var result = await cs.EnsureCertificatesTrustedAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(result.Success);
        var expectedMessage = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorStrings.CertificateGenerationFailed, EnsureCertificateResult.ErrorCreatingTheCertificate);
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message == expectedMessage);
    }

    private ServiceProvider CreateServiceProvider(TemporaryWorkspace workspace, TestCertificateToolRunner toolRunner, bool nonInteractive = false, IEnvironment? environment = null)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CertificateToolRunnerFactory = _ => toolRunner;
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive);
            };
            options.CertificateServiceFactory = sp =>
            {
                var interactiveService = sp.GetRequiredService<IInteractionService>();
                var telemetry = sp.GetRequiredService<AspireCliTelemetry>();
                var hostEnvironment = sp.GetRequiredService<ICliHostEnvironment>();
                var executionContext = sp.GetRequiredService<CliExecutionContext>();
                return new CertificateService(toolRunner, interactiveService, telemetry, hostEnvironment, executionContext, environment ?? sp.GetRequiredService<IEnvironment>());
            };
        });

        return services.BuildServiceProvider();
    }

    private static CertificateTrustResult CreateNoCertsResult()
    {
        return new CertificateTrustResult
        {
            HasCertificates = false,
            TrustLevel = null,
            Certificates = []
        };
    }

    private static CertificateTrustResult CreateTrustResult(CertificateManager.TrustLevel? trustLevel)
    {
        if (trustLevel is null)
        {
            return CreateNoCertsResult();
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

    private static void AssertSslCertDirContainsDevCertsTrustPath(IDictionary<string, string> environmentVariables)
    {
        Assert.True(environmentVariables.ContainsKey("SSL_CERT_DIR"));
        Assert.Contains(CertificateHelpers.GetDevCertsTrustPath(), environmentVariables["SSL_CERT_DIR"].Split(Path.PathSeparator));
    }
}
