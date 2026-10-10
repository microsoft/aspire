// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;

namespace Aspire.Cli.Tests.Projects;

public class AppHostConfigurationProjectorTests
{
    private const string ConfiguredImage = "example.com/aspire-tunnel:configured";

    [Fact]
    public async Task ApplyEnvironmentVariablesAsync_ProjectsConfiguredValue()
    {
        var appHostDirectory = new DirectoryInfo("/app");
        var configurationService = new TestConfigurationService
        {
            OnGetConfigurationFromDirectory = (key, directory) =>
            {
                Assert.Equal(AspireConfigContainerTunnel.BaseImageConfigPath, key);
                Assert.Equal(appHostDirectory, directory);
                return ConfiguredImage;
            }
        };
        var environmentVariables = new Dictionary<string, string>();
        var projector = new AppHostConfigurationProjector(configurationService, new TestEnvironment());

        await projector.ApplyEnvironmentVariablesAsync(environmentVariables, appHostDirectory);

        Assert.Equal(ConfiguredImage, environmentVariables[KnownConfigNames.ContainerTunnelBaseImage]);
    }

    [Fact]
    public async Task ApplyEnvironmentVariablesAsync_DoesNotReplaceExplicitLaunchValue()
    {
        const string launchImage = "example.com/aspire-tunnel:launch";
        var configurationService = new TestConfigurationService
        {
            OnGetConfiguration = _ => ConfiguredImage
        };
        var environmentVariables = new Dictionary<string, string>
        {
            [KnownConfigNames.ContainerTunnelBaseImage] = launchImage
        };
        var projector = new AppHostConfigurationProjector(configurationService, new TestEnvironment());

        await projector.ApplyEnvironmentVariablesAsync(environmentVariables, new DirectoryInfo("/app"));

        Assert.Equal(launchImage, environmentVariables[KnownConfigNames.ContainerTunnelBaseImage]);
    }

    [Fact]
    public async Task ApplyEnvironmentVariablesAsync_DoesNotReplaceAmbientValue()
    {
        const string ambientImage = "example.com/aspire-tunnel:ambient";
        var configurationService = new TestConfigurationService
        {
            OnGetConfiguration = _ => ConfiguredImage
        };
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [KnownConfigNames.ContainerTunnelBaseImage] = ambientImage
        });
        var environmentVariables = new Dictionary<string, string>();
        var projector = new AppHostConfigurationProjector(configurationService, environment);

        await projector.ApplyEnvironmentVariablesAsync(environmentVariables, new DirectoryInfo("/app"));

        Assert.False(environmentVariables.ContainsKey(KnownConfigNames.ContainerTunnelBaseImage));
    }

    [Fact]
    public async Task ApplyEnvironmentVariablesAsync_IgnoresEmptyConfiguredValue()
    {
        var configurationService = new TestConfigurationService
        {
            OnGetConfiguration = _ => string.Empty
        };
        var environmentVariables = new Dictionary<string, string>();
        var projector = new AppHostConfigurationProjector(configurationService, new TestEnvironment());

        await projector.ApplyEnvironmentVariablesAsync(environmentVariables, new DirectoryInfo("/app"));

        Assert.False(environmentVariables.ContainsKey(KnownConfigNames.ContainerTunnelBaseImage));
    }
}
