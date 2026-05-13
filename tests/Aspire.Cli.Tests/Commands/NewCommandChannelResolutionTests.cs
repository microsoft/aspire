// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Commands;

/// <summary>
/// Behavioral guard on <see cref="NewCommand"/>'s channel resolution. PR1 removed the
/// global-channel read fallback (<c>IConfigurationService.GetConfigurationAsync("channel", ...)</c>)
/// from the template-version resolver. <see cref="NewCommand"/> now sources its channel
/// preference from <c>--channel</c>, <see cref="CliExecutionContext.IdentityChannel"/>, and the
/// registered channel set only — never from the global settings file. This test pins that
/// contract so a regression can't quietly re-introduce cross-route channel contamination.
/// </summary>
public class NewCommandChannelResolutionTests(ITestOutputHelper outputHelper)
{
    /// <summary>
    /// Negative-shape tripwire: <c>aspire new</c> must never read the <c>channel</c> key from
    /// the global <see cref="IConfigurationService"/>. The injected configuration service
    /// throws on any <c>GetConfigurationAsync</c> or <c>GetConfigurationFromDirectoryAsync</c>
    /// call whose key is <c>channel</c>; if the command invokes either, the test fails with
    /// the thrown message. Mirrors <c>InitCommand_DoesNotConsultGlobalConfigurationServiceForChannelKey</c>.
    /// </summary>
    [Fact]
    public async Task NewCommand_DoesNotConsultGlobalConfigurationServiceForChannelKey()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var tripwireConfigService = new global::Aspire.Cli.Tests.TestServices.TestConfigurationService
        {
            OnGetConfiguration = key =>
            {
                if (string.Equals(key, "channel", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "aspire new must not consult IConfigurationService for the 'channel' key. " +
                        "Channel resolution sources from --channel and CliExecutionContext.IdentityChannel only.");
                }
                return null;
            },
            OnGetConfigurationFromDirectory = (key, _) =>
            {
                if (string.Equals(key, "channel", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "aspire new must not consult IConfigurationService.GetConfigurationFromDirectoryAsync " +
                        "for the 'channel' key. Channel resolution sources from --channel and CliExecutionContext.IdentityChannel only.");
                }
                return null;
            }
        };

        string? capturedTemplateVersion = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ConfigurationServiceFactory = _ => tripwireConfigService;

            // Pin a single Implicit channel so the template resolver has a definite fall-through
            // target. The assertion is about IConfigurationService NOT being touched for "channel";
            // the channel set itself is incidental.
            options.PackagingServiceFactory = _ =>
            {
                var fakeCache = new FakeNuGetPackageCache
                {
                    GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                        Task.FromResult<IEnumerable<NuGetPackage>>(
                            [new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "13.3.0" }])
                };
                var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache);
                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([implicitChannel])
                };
            };

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, _, _, _, _, _) =>
                {
                    capturedTemplateVersion = version;
                    return (0, version);
                };
                runner.NewProjectAsyncCallback = (_, _, outputPath, _, _) =>
                {
                    Directory.CreateDirectory(outputPath);
                    return 0;
                };
                return runner;
            };
        });

        using var serviceProvider = services.BuildServiceProvider();
        var newCommand = serviceProvider.GetRequiredService<NewCommand>();

        var parseResult = newCommand.Parse("new aspire-starter --name TestApp --output ./output --use-redis-cache --test-framework None");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        // The template version came from the Implicit channel's package cache. If the tripwire
        // had been triggered, the run would have failed before reaching install.
        Assert.Equal("13.3.0", capturedTemplateVersion);
    }
}

