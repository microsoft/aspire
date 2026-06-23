// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Certificates;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class CertificatesCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CertificatesCommand_Help_ShowsCertificatesSubcommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("certs --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task CertificatesCommand_CleanSubcommand_ShowsInHelp()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("certs clean --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task CertificatesCommand_TrustSubcommand_ShowsInHelp()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("certs trust --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task CertificatesCommand_TrustSubcommand_ReturnsSuccessForNonInteractiveLinuxPartialTrust()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var toolRunner = new TestCertificateToolRunner
        {
            TrustHttpCertificateCallback = () => EnsureCertificateResult.PartiallyFailedToTrustTheCertificate,
            CheckHttpCertificateCallback = () => new CertificateTrustResult
            {
                HasCertificates = true,
                TrustLevel = CertificateManager.TrustLevel.Partial,
                Certificates =
                [
                    new DevCertInfo
                    {
                        Version = 5,
                        TrustLevel = CertificateManager.TrustLevel.Partial,
                        IsHttpsDevelopmentCertificate = true,
                        ValidityNotBefore = DateTimeOffset.Now.AddDays(-1),
                        ValidityNotAfter = DateTimeOffset.Now.AddDays(365)
                    }
                ]
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CertificateToolRunnerFactory = _ => toolRunner;
            options.CliHostEnvironmentFactory = sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                return new CliHostEnvironment(configuration, nonInteractive: true);
            };
            options.CertificateServiceFactory = sp =>
            {
                var telemetry = sp.GetRequiredService<AspireCliTelemetry>();
                var hostEnvironment = sp.GetRequiredService<ICliHostEnvironment>();
                var executionContext = sp.GetRequiredService<CliExecutionContext>();
                return new CertificateService(toolRunner, interactionService, telemetry, hostEnvironment, executionContext, new TestEnvironment { IsLinux = true });
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("certs trust --non-interactive");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message.Contains("partially trusted", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(interactionService.DisplayedSuccess);
    }
}
