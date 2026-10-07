// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Configuration;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetPackageOperationConfigurationTests(ITestOutputHelper outputHelper)
{
    private static readonly byte[] s_sourceIdentityKey = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

    [Fact]
    public async Task StableChannelUsesAmbientConfigurationWithoutGeneratingNuGetOrgConfig()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteAmbientConfig(workspace.WorkspaceRoot);
        var service = CreateService();

        using var configuration = await service.CreatePackageOperationConfigurationAsync(
            workspace.WorkspaceRoot,
            [new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)],
            restrictToSelectedSources: false,
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
        var service = CreateService();

        string overlayDirectory;
        using (var configuration = await service.CreatePackageOperationConfigurationAsync(
            workspace.WorkspaceRoot,
            [
                new PackageMapping("Aspire*", channelSource),
                new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)
            ],
            restrictToSelectedSources: false,
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

    [Theory]
    [InlineData(false, "http://example.test/private/v3/index.json")]
    [InlineData(true, "http://example.test/private/v3/index.json")]
    [InlineData(false, "HTTP://EXAMPLE.TEST/private/v3/index.json")]
    [InlineData(true, "HTTP://EXAMPLE.TEST/private/v3/index.json")]
    [InlineData(false, "http://example.test:80/private/v3/index.json")]
    [InlineData(true, "http://example.test:80/private/v3/index.json")]
    [InlineData(false, "http://example.test/private/./v3/index.json")]
    [InlineData(true, "http://example.test/private/./v3/index.json")]
    [InlineData(false, "http://example.test/private/v3/%69ndex.json")]
    [InlineData(true, "http://example.test/private/v3/%69ndex.json")]
    public async Task SourceOverridePreservesSelectedAliasCredentialsAndTransportSettings(bool disabled, string sourceOverride)
    {
        const string sourceUrl = "http://example.test/private/v3/index.json";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var ambientPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        var ambientConfig = $"""
            <configuration>
              <config>
                <add key="signatureValidationMode" value="require" />
              </config>
              <packageSources>
                <clear />
                <add key="private" value="{sourceUrl}" protocolVersion="3" allowInsecureConnections="true" disableTLSCertificateValidation="true" />
                <add key="unrelated" value="https://example.test/unrelated/v3/index.json" />
              </packageSources>
              <auditSources>
                <clear />
                <add key="audit" value="https://example.test/audit/v3/index.json" />
              </auditSources>
              <trustedSigners>
                <clear />
                <author name="trusted-author">
                  <certificate fingerprint="{new string('A', 64)}" hashAlgorithm="SHA256" allowUntrustedRoot="false" />
                </author>
              </trustedSigners>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="test-user" />
                  <add key="ClearTextPassword" value="test-password" />
                </private>
              </packageSourceCredentials>
              <disabledPackageSources>
                {(disabled ? """<add key="private" value="true" />""" : "")}
              </disabledPackageSources>
              <packageSourceMapping>
                <packageSource key="unrelated">
                  <package pattern="CommunityToolkit.Aspire.Hosting.Custom" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        File.WriteAllText(ambientPath, ambientConfig);

        using var configuration = await CreateService().CreatePackageOperationConfigurationAsync(
            workspace.WorkspaceRoot,
            PackageSourceOverrideMappings.CreateForSourceOnlyOperations(sourceOverride),
            restrictToSelectedSources: true,
            TestContext.Current.CancellationToken);

        Assert.Null(configuration.ExplicitConfigFile);
        Assert.Equal(workspace.WorkspaceRoot.FullName, configuration.EffectiveWorkingDirectory.Parent!.FullName);
        var settings = Settings.LoadDefaultSettings(configuration.EffectiveWorkingDirectory.FullName);
        var selectedSource = Assert.Single(new PackageSourceProvider(settings).LoadPackageSources(), source => source.IsEnabled);
        Assert.Equal("private", selectedSource.Name);
        Assert.Equal(sourceUrl, selectedSource.Source);
        Assert.True(selectedSource.AllowInsecureConnections);
        Assert.True(selectedSource.DisableTLSCertificateValidation);
        Assert.Equal(3, selectedSource.ProtocolVersion);
        Assert.NotNull(selectedSource.Credentials);
        Assert.Equal("test-user", selectedSource.Credentials.Username);
        Assert.Equal("test-password", selectedSource.Credentials.Password);
        Assert.Equal("require", SettingsUtility.GetConfigValue(settings, "signatureValidationMode"));
        Assert.Equal(["audit"], new PackageSourceProvider(settings).LoadAuditSources().Select(source => source.Name));
        var signer = Assert.Single(settings.GetSection("trustedSigners")!.Items.OfType<TrustedSignerItem>());
        Assert.Equal("trusted-author", signer.Name);
        var mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        Assert.Equal(["private"], mapping.GetConfiguredPackageSources("Aspire.Hosting.Redis"));
        Assert.Equal(["private"], mapping.GetConfiguredPackageSources("CommunityToolkit.Aspire.Hosting.Custom"));
        Assert.Equal(ambientConfig, File.ReadAllText(ambientPath));

        var overlay = XDocument.Load(configuration.ConfigurationFile!.FullName);
        Assert.Equal(
            ["disabledPackageSources", "packageSourceMapping"],
            overlay.Root!.Elements().Select(element => element.Name.LocalName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SourceOverrideAddsAndRestrictsANewSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteAmbientConfig(workspace.WorkspaceRoot);
        const string sourceUrl = "https://example.test/selected/v3/index.json";

        using var configuration = await CreateService().CreatePackageOperationConfigurationAsync(
            workspace.WorkspaceRoot,
            PackageSourceOverrideMappings.CreateForSourceOnlyOperations(sourceUrl),
            restrictToSelectedSources: true,
            TestContext.Current.CancellationToken);

        var settings = Settings.LoadDefaultSettings(configuration.EffectiveWorkingDirectory.FullName);
        var source = Assert.Single(new PackageSourceProvider(settings).LoadPackageSources(), source => source.IsEnabled);
        Assert.Equal("aspire-package-search", source.Name);
        Assert.Equal(sourceUrl, source.Source);
        Assert.Null(source.Credentials);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllWritersMaterializeTheComposedSourceRestriction(bool restricted)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteAmbientConfig(workspace.WorkspaceRoot);
        var service = CreateService();
        var configuration = service.BuildConfiguration(
            workspace.WorkspaceRoot,
            workloadId: "writer-test",
            PackageSourceOverrideMappings.CreateForSourceOnlyOperations("https://example.test/selected/v3/index.json"),
            restrictToSelectedSources: restricted,
            hasAuthoritativeAspirePolicy: true,
            cancellationToken: TestContext.Current.CancellationToken);

        var overlay = configuration.Overlay;
        Assert.NotNull(overlay);
        Assert.Equal(restricted, overlay.ClearDisabledPackageSources);
        Assert.Equal(restricted ? ["internal"] : [], overlay.DisabledPackageSourceKeys);
        Assert.Equal(
            restricted ? ["aspire-writer-test"] : ["aspire-writer-test", "internal"],
            overlay.PackageSourceMappings.Select(mapping => mapping.SourceKey).Order(StringComparer.Ordinal));

        var persistentPath = Path.Combine(workspace.CreateDirectory("persistent").FullName, "NuGet.Config");
        service.WriteNuGetConfig(configuration, persistentPath, globalPackagesFolder: null, TestContext.Current.CancellationToken);
        using var temporary = await service.WriteTemporaryOverlayAsync(
            configuration,
            workspace.WorkspaceRoot,
            globalPackagesFolder: null,
            TestContext.Current.CancellationToken);
        Assert.NotNull(temporary);
        Assert.Equal(XDocument.Load(persistentPath).ToString(), XDocument.Load(temporary.ConfigFile.FullName).ToString());

        await temporary.RegenerateAsync(path =>
            service.WriteNuGetConfig(configuration, path, globalPackagesFolder: null, TestContext.Current.CancellationToken));
        Assert.Equal(XDocument.Load(persistentPath).ToString(), XDocument.Load(temporary.ConfigFile.FullName).ToString());
    }

    private static BundleNuGetService CreateService()
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
        return service;
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
