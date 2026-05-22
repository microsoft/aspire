// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using System.Xml.Linq;

namespace Aspire.Cli.Tests.Templating;

/// <summary>
/// Channel-resolution behavior for <see cref="TemplateNuGetConfigService"/>.
/// None of the channel-resolving entry points
/// (<see cref="TemplateNuGetConfigService.PromptToCreateOrUpdateNuGetConfigAsync(string?, string, CancellationToken)"/>,
/// <see cref="TemplateNuGetConfigService.CreateOrUpdateNuGetConfigWithoutPromptAsync(string?, string, CancellationToken)"/>)
/// may resolve a channel by reading from a global identity-channel source; channel input
/// must come from the caller-supplied argument or fall back to the implicit channel only.
/// </summary>
public class TemplateNuGetConfigServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CreateOrUpdateNuGetConfigForSourceOverrideAsync_CreatesSelfContainedConfigWithoutAmbientSources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDirectory = workspace.WorkspaceRoot.CreateSubdirectory("output");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="ambient-private" value="https://private.example/v3/index.json" />
              </packageSources>
              <disabledPackageSources>
                <add key="ambient-private" value="true" />
              </disabledPackageSources>
              <packageSourceCredentials>
                <ambient-private>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="secret" />
                </ambient-private>
              </packageSourceCredentials>
              <packageSourceMapping>
                <packageSource key="ambient-private">
                  <package pattern="Contoso.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var service = CreateService();
        const string sourceOverride = "/tmp/aspire-pr-hive/packages";

        Assert.True(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride, channelName: null, outputDirectory.FullName, CancellationToken.None));

        var doc = XDocument.Load(Path.Combine(outputDirectory.FullName, "nuget.config"));
        Assert.Contains(doc.Root!.Element("packageSources")!.Elements("clear"), _ => true);
        Assert.Contains(doc.Root!.Element("packageSources")!.Elements("add"), e => (string?)e.Attribute("value") == sourceOverride);
        Assert.Contains(doc.Root!.Element("packageSources")!.Elements("add"), e => (string?)e.Attribute("value") == PackageSources.NuGetOrg);
        Assert.DoesNotContain(doc.Descendants("add"), e => (string?)e.Attribute("value") == "https://private.example/v3/index.json");
        Assert.Null(doc.Root!.Element("disabledPackageSources"));
        Assert.Null(doc.Root!.Element("packageSourceCredentials"));
        Assert.Empty(GetPackagePatternsForSource(doc, "ambient-private"));
    }

    [Fact]
    public async Task CreateOrUpdateNuGetConfigForSourceOverrideAsync_PreservesRequestedChannelFallbackMappings()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDirectory = workspace.WorkspaceRoot.CreateSubdirectory("output");
        const string sourceOverride = "/tmp/aspire-pr-hive/packages";
        const string channelAspireSource = "https://example.invalid/aspire";
        const string communitySource = "https://example.invalid/community";
        const string fallbackSource = "https://example.invalid/fallback";

        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var channel = PackageChannel.CreateExplicitChannel(
                    "daily",
                    PackageChannelQuality.Both,
                    [
                        new PackageMapping("Aspire*", channelAspireSource),
                        new PackageMapping("CommunityToolkit*", communitySource),
                        new PackageMapping(PackageMapping.AllPackages, fallbackSource),
                    ],
                    new FakeNuGetPackageCache(),
                    features: new TestFeatures());
                return Task.FromResult<IEnumerable<PackageChannel>>([channel]);
            }
        };
        var service = CreateService(packagingService: packagingService);

        Assert.True(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride, channelName: "daily", outputDirectory.FullName, CancellationToken.None));

        var doc = XDocument.Load(Path.Combine(outputDirectory.FullName, "nuget.config"));
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, sourceOverride));
        Assert.Equal(["CommunityToolkit*"], GetPackagePatternsForSource(doc, communitySource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, fallbackSource));
        Assert.Empty(GetPackagePatternsForSource(doc, channelAspireSource));
        Assert.Empty(GetPackagePatternsForSource(doc, PackageSources.NuGetOrg));
    }

    [Fact]
    public async Task CreateOrUpdateNuGetConfigForSourceOverrideAsync_UpdatesOnlyProjectLocalConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDirectory = workspace.WorkspaceRoot.CreateSubdirectory("output");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="parent-private" value="https://parent.example/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory.FullName, "nuget.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="project-local" value="https://project.example/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="project-local">
                  <package pattern="Project.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var service = CreateService();
        const string sourceOverride = "/tmp/aspire-pr-hive/packages";

        Assert.True(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride, channelName: null, outputDirectory.FullName, CancellationToken.None));

        var doc = XDocument.Load(Path.Combine(outputDirectory.FullName, "nuget.config"));
        Assert.Contains(doc.Root!.Element("packageSources")!.Elements("add"), e => (string?)e.Attribute("value") == "https://project.example/v3/index.json");
        Assert.DoesNotContain(doc.Root!.Element("packageSources")!.Elements("add"), e => (string?)e.Attribute("value") == "https://parent.example/v3/index.json");
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, sourceOverride));
        Assert.Equal(["Project.*", PackageMapping.AllPackages], GetPackagePatternsForSource(doc, "project-local"));
    }

    [Fact]
    public async Task CreateOrUpdateNuGetConfigForSourceOverrideAsync_NullSourceShortCircuits()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Channel lookup should not run without a source override.")
        };
        var service = CreateService(packagingService: packagingService);

        Assert.False(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride: null, channelName: "daily", workspace.WorkspaceRoot.FullName, CancellationToken.None));
        Assert.False(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride: "", channelName: "daily", workspace.WorkspaceRoot.FullName, CancellationToken.None));
        Assert.False(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride: "   ", channelName: "daily", workspace.WorkspaceRoot.FullName, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://user:token@example.invalid/v3/index.json")]
    [InlineData("https://example.invalid/v3/index.json?sig=token")]
    [InlineData("https://example.invalid/v3/index.json#token")]
    public async Task CreateOrUpdateNuGetConfigForSourceOverrideAsync_CredentialBearingHttpSourceThrows(string sourceOverride)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride, channelName: null, workspace.WorkspaceRoot.FullName, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")));
    }

    [Fact]
    public async Task PromptToCreateOrUpdateNuGetConfigAsync_NullChannelName_ShortCircuits()
    {
        // Null/whitespace channelName must short-circuit without consulting any
        // ambient channel source. No exception, no implicit-channel work requested.
        var service = CreateService();

        await service.PromptToCreateOrUpdateNuGetConfigAsync(channelName: null, outputPath: Directory.CreateTempSubdirectory().FullName, CancellationToken.None);
        await service.PromptToCreateOrUpdateNuGetConfigAsync(channelName: "", outputPath: Directory.CreateTempSubdirectory().FullName, CancellationToken.None);
        await service.PromptToCreateOrUpdateNuGetConfigAsync(channelName: "   ", outputPath: Directory.CreateTempSubdirectory().FullName, CancellationToken.None);
    }

    [Fact]
    public async Task CreateOrUpdateNuGetConfigWithoutPromptAsync_NullChannelName_ShortCircuits()
    {
        var service = CreateService();

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            // Null/whitespace inputs must short-circuit and return false without
            // resolving a channel from any ambient source.
            Assert.False(await service.CreateOrUpdateNuGetConfigWithoutPromptAsync(channelName: null, outputPath: dir.FullName, CancellationToken.None));
            Assert.False(await service.CreateOrUpdateNuGetConfigWithoutPromptAsync(channelName: "", outputPath: dir.FullName, CancellationToken.None));
            Assert.False(await service.CreateOrUpdateNuGetConfigWithoutPromptAsync(channelName: "   ", outputPath: dir.FullName, CancellationToken.None));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string[] GetPackagePatternsForSource(XDocument doc, string source)
    {
        var packageSourceMapping = doc.Root!.Element("packageSourceMapping");
        if (packageSourceMapping is null)
        {
            return [];
        }

        return packageSourceMapping
            .Elements("packageSource")
            .Where(e => string.Equals((string?)e.Attribute("key"), source, StringComparison.OrdinalIgnoreCase))
            .Elements("package")
            .Select(e => (string?)e.Attribute("pattern"))
            .Where(pattern => pattern is not null)
            .Select(pattern => pattern!)
            .ToArray();
    }

    private static TemplateNuGetConfigService CreateService(
        TestPackagingService? packagingService = null,
        CliExecutionContext? executionContext = null)
    {
        return new TemplateNuGetConfigService(
            new TestInteractionService(),
            executionContext ?? TestExecutionContextFactory.CreateTestContext(),
            packagingService ?? MockPackagingServiceFactory.Create());
    }
}
