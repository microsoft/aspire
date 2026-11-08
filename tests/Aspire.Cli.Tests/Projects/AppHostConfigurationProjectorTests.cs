// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;

namespace Aspire.Cli.Tests.Projects;

public class AppHostConfigurationProjectorTests(ITestOutputHelper outputHelper)
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

        await projector.ApplyEnvironmentVariablesAsync(environmentVariables, new FileInfo("/app/apphost.cs"), appHostDirectory);

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

        await projector.ApplyEnvironmentVariablesAsync(environmentVariables, new FileInfo("/app/apphost.cs"), new DirectoryInfo("/app"));

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

        await projector.ApplyEnvironmentVariablesAsync(environmentVariables, new FileInfo("/app/apphost.cs"), new DirectoryInfo("/app"));

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

        await projector.ApplyEnvironmentVariablesAsync(environmentVariables, new FileInfo("/app/apphost.cs"), new DirectoryInfo("/app"));

        Assert.False(environmentVariables.ContainsKey(KnownConfigNames.ContainerTunnelBaseImage));
    }

    [Fact]
    public async Task ApplyEnvironmentVariablesAsync_ProjectsConfiguredAppHostName()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = WriteNamedAppHost(workspace);
        var environmentVariables = new Dictionary<string, string>();
        var projector = new AppHostConfigurationProjector(new TestConfigurationService(), new TestEnvironment());

        await projector.ApplyEnvironmentVariablesAsync(environmentVariables, appHostFile, appHostFile.Directory!);

        Assert.Equal("Contoso Shop", environmentVariables[KnownConfigNames.AppHostName]);
    }

    [Fact]
    public async Task ApplyEnvironmentVariablesAsync_DoesNotReplaceExplicitAppHostName()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = WriteNamedAppHost(workspace);
        var environmentVariables = new Dictionary<string, string>
        {
            [KnownConfigNames.AppHostName] = "Launch name"
        };
        var projector = new AppHostConfigurationProjector(new TestConfigurationService(), new TestEnvironment());

        await projector.ApplyEnvironmentVariablesAsync(environmentVariables, appHostFile, appHostFile.Directory!);

        Assert.Equal("Launch name", environmentVariables[KnownConfigNames.AppHostName]);
    }

    private static FileInfo WriteNamedAppHost(TemporaryWorkspace workspace)
    {
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "src", "apphost.cs"));
        appHostFile.Directory!.Create();
        File.WriteAllText(appHostFile.FullName, "");
        File.WriteAllText(
            Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName),
            """{ "appHost": { "path": "src/apphost.cs", "name": "Contoso Shop" } }""");
        return appHostFile;
    }
}
