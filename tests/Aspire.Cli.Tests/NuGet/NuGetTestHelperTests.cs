// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using global::NuGet.Configuration;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetTestHelperTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void DefaultSettingsExcludeHostConfiguration()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var expectedConfigPath = Path.Combine(AppContext.BaseDirectory, "NuGetTestSettings.config");
        var client = NuGetTestHelper.CreateClient();

        var snapshot = client.GetSettings(workspace.Path, new byte[NuGetSourceIdentity.KeySizeInBytes]);
        var settings = NuGetTestHelper.LoadSettings(workspace.Path);

        Assert.Equal([expectedConfigPath], snapshot.ConfigPaths);
        Assert.Equal([expectedConfigPath], settings.GetConfigFilePaths());
        var source = Assert.Single(snapshot.Sources);
        Assert.Equal("nuget.org", source.Name);
        Assert.Equal(NuGetSourceIdentity.Compute(PackageSources.NuGetOrg, snapshot.SourceIdentityKey), source.Identity);
        Assert.Equal([PackageSources.NuGetOrg], NuGetTestHelper.GetEligiblePackageSources(workspace.Path, "Example.Dependency"));
        Assert.Empty(snapshot.PackageSourceMappings);
        Assert.Empty(snapshot.DisabledPackageSourceKeys);
        Assert.Empty(workspace.WorkspaceRoot.EnumerateFiles());
    }

    [Fact]
    public async Task ExplicitMachineSettingsPreserveWorkspaceHierarchy()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var machineDirectory = workspace.CreateDirectory("machine");
        var appHostDirectory = workspace.CreateDirectory("apphost");
        var configPath = Path.Combine(workspace.Path, "NuGet.Config");
        const string projectConfig = """
            <configuration>
              <packageSources>
                <add key="project" value="https://project.example/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="project"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        await File.WriteAllTextAsync(configPath, projectConfig);
        var machineConfigPath = Path.Combine(machineDirectory.FullName, "NuGet.Config");
        await File.WriteAllTextAsync(machineConfigPath, """
            <configuration>
              <packageSources>
                <add key="machine" value="https://machine.example/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="machine"><package pattern="Aspire*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var machineSettings = new TestMachineWideSettings(new Settings(machineDirectory.FullName, "NuGet.Config", isMachineWide: true));
        var client = NuGetTestHelper.CreateClient(machineSettings);

        var snapshot = client.GetSettings(appHostDirectory.FullName, new byte[NuGetSourceIdentity.KeySizeInBytes]);
        var settings = NuGetTestHelper.LoadSettings(appHostDirectory.FullName, machineSettings);

        Assert.Equal([configPath, Path.Combine(AppContext.BaseDirectory, "NuGetTestSettings.config"), machineConfigPath], snapshot.ConfigPaths);
        Assert.Equal(snapshot.ConfigPaths, settings.GetConfigFilePaths());
        Assert.Equal(["project", "nuget.org", "machine"], snapshot.Sources.Select(static source => source.Name));
        Assert.Equal(["https://machine.example/v3/index.json"],
            NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Aspire.Hosting", machineSettings));
        Assert.Equal(["https://project.example/v3/index.json"],
            NuGetTestHelper.GetEligiblePackageSources(appHostDirectory.FullName, "Example.Dependency", machineSettings));
        Assert.Equal(projectConfig, await File.ReadAllTextAsync(configPath));
    }
}
