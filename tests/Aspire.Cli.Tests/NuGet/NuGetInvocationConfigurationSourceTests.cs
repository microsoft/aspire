// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetInvocationConfigurationSourceTests(ITestOutputHelper outputHelper)
{
    private static readonly byte[] s_sourceIdentityKey = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

    [Fact]
    public async Task StableChannelUsesAmbientConfigurationWithoutGeneratingNuGetOrgConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteAmbientConfig(workspace.WorkspaceRoot);
        var source = CreateSource();

        using var configuration = await source.CreateAmbientOverlayAsync(
            workspace.WorkspaceRoot,
            [new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)],
            TestContext.Current.CancellationToken);

        Assert.Equal(workspace.WorkspaceRoot.FullName, configuration.EffectiveWorkingDirectory.FullName);
        Assert.Null(configuration.ConfigurationFile);
        Assert.Null(configuration.ExplicitConfigFile);
    }

    [Fact]
    public async Task ChannelOverlayPreservesAmbientFallbackWithoutAddingNuGetOrg()
    {
        const string channelSource = "https://example.test/aspire/v3/index.json";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteAmbientConfig(workspace.WorkspaceRoot);
        var source = CreateSource();

        string overlayDirectory;
        using (var configuration = await source.CreateAmbientOverlayAsync(
            workspace.WorkspaceRoot,
            [
                new PackageMapping("Aspire*", channelSource),
                new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)
            ],
            TestContext.Current.CancellationToken))
        {
            Assert.NotNull(configuration.ConfigurationFile);
            Assert.Null(configuration.ExplicitConfigFile);
            Assert.Equal(workspace.WorkspaceRoot.FullName, configuration.EffectiveWorkingDirectory.Parent!.FullName);

            overlayDirectory = configuration.EffectiveWorkingDirectory.FullName;
            var document = XDocument.Load(configuration.ConfigurationFile.FullName);
            var sources = document.Root!
                .Element("packageSources")!
                .Elements("add")
                .Select(element => (string)element.Attribute("value")!)
                .ToArray();
            Assert.Equal([channelSource], sources);

            var mappings = document.Root!
                .Element("packageSourceMapping")!
                .Elements("packageSource")
                .ToDictionary(
                    element => (string)element.Attribute("key")!,
                    element => element.Elements("package")
                        .Select(package => (string)package.Attribute("pattern")!)
                        .ToArray());
            Assert.Equal([PackageMapping.AllPackages], mappings["internal"]);
            Assert.Equal(["Aspire*"], mappings["aspire-package-search"]);
        }

        Assert.False(Directory.Exists(overlayDirectory));
    }

    private static NuGetInvocationConfigurationSource CreateSource()
    {
        var nuGetClient = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);
        var service = new BundleNuGetService(
            NullLogger<BundleNuGetService>.Instance,
            nuGetClient)
        {
            SourceIdentityKeyFactory = static () => s_sourceIdentityKey
        };
        return new NuGetInvocationConfigurationSource(service);
    }

    private static void WriteAmbientConfig(DirectoryInfo directory)
    {
        File.WriteAllText(
            Path.Combine(directory.FullName, "NuGet.Config"),
            """
            <configuration>
              <packageSources>
                <clear />
                <add key="internal" value="https://example.test/internal/v3/index.json" />
              </packageSources>
            </configuration>
            """);
    }
}
