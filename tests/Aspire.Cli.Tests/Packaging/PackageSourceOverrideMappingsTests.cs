// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Configuration;

namespace Aspire.Cli.Tests.Packaging;

public class PackageSourceOverrideMappingsTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("Aspire*", "Aspire.Hosting.Redis", true)]
    [InlineData("aspire*", "Aspire.Hosting.Redis", true)]
    [InlineData("Aspire.Hosting.Redis", "aspire.hosting.redis", true)]
    [InlineData("Aspire.Hosting.Redis", "Aspire.Hosting.PostgreSQL", false)]
    [InlineData("Aspire*", "CommunityToolkit.Aspire.Hosting.Redis", false)]
    [InlineData("*", "CommunityToolkit.Aspire.Hosting.Redis", true)]
    public void MatchesPackage_UsesNuGetMappingPatternSemantics(
        string packagePattern,
        string packageName,
        bool expected)
    {
        var nativeMapping = new PackageSourceMapping(new Dictionary<string, IReadOnlyList<string>>
        {
            ["selected"] = [packagePattern]
        });

        Assert.Equal(expected ? ["selected"] : [], nativeMapping.GetConfiguredPackageSources(packageName));
        Assert.Equal(expected, PackageSourceOverrideMappings.MatchesPackage(packagePattern, packageName));
    }

    [Fact]
    public void CredentialBearingSourceOverride_IsRejected()
    {
        const string source = "https://user:p#word@host/";

        Assert.True(PackageSourceOverrideMappings.HasCredentialMaterial(source));
        Assert.Throws<ArgumentException>(() =>
            PackageSourceOverrideMappings.Create(source, requestedChannel: null, nugetServiceIndexOverride: source));
        Assert.Throws<ArgumentException>(() =>
            PackageSourceOverrideMappings.CreateForSourceOnlyOperations(source));
    }

    [Fact]
    public void Create_ExactPackagePatternKeepsSourceEligibleForAspireClosure()
    {
        const string source = "https://example.com/integration";
        const string packageId = "CommunityToolkit.Aspire.Hosting.Redis";

        var mappings = PackageSourceOverrideMappings.Create(
            source,
            requestedChannel: null,
            nugetServiceIndexOverride: null,
            packagePattern: packageId);

        Assert.Equal(
            [packageId, PackageSourceOverrideMappings.DefaultPackagePattern, PackageMapping.AllPackages],
            mappings
                .Where(mapping => PackageSourceIdentity.Comparer.Equals(mapping.Source, source))
                .Select(static mapping => mapping.PackageFilter));
    }

    [Theory]
    [InlineData("HTTPS://EXAMPLE.TEST/feed/v3/index.json", "Aspire*")]
    [InlineData("HTTPS://EXAMPLE.TEST/feed/v3/index.json", "aspire*")]
    [InlineData("https://example.test:443/feed/v3/index.json", "Aspire*")]
    [InlineData("https://example.test/feed/./v3/index.json", "Aspire*")]
    [InlineData("https://example.test/feed/v3/%69ndex.json", "Aspire*")]
    [InlineData("https://example.test/feed/v3/index.json", "aspire*")]
    public async Task Create_EquivalentMappingsPreserveNativeEligibilityAndCacheIdentity(string source, string channelPattern)
    {
        const string canonicalSource = "https://example.test/feed/v3/index.json";
        const string packageId = "CommunityToolkit.Aspire.Hosting.Redis";
        var channel = CreateSourceIsolatedChannel(canonicalSource, channelPattern);

        var mappings = PackageSourceOverrideMappings.Create(source, channel, nugetServiceIndexOverride: null, packageId);
        var canonicalMappings = PackageSourceOverrideMappings.Create(canonicalSource, channel, nugetServiceIndexOverride: null, packageId);

        Assert.Equal(
        [
            (packageId, source),
            ("Aspire*", source),
            (PackageMapping.AllPackages, source),
            (PackageMapping.AllPackages, PackageSources.NuGetOrg)
        ],
            mappings.Select(mapping => (mapping.PackageFilter, mapping.Source)));
        Assert.Equal(
            IntegrationRestorePlanResolver.CreateGlobalPackagesFolderIdentity([canonicalSource], canonicalMappings),
            IntegrationRestorePlanResolver.CreateGlobalPackagesFolderIdentity([source], mappings));

        using var config = await NuGetTestHelper.CreateStandaloneConfigurationAsync(mappings);
        var settings = Settings.LoadSpecificSettings(config.ConfigFile.DirectoryName!, config.ConfigFile.Name);
        var nativeMapping = PackageSourceMapping.GetPackageSourceMapping(settings);

        Assert.Equal(["aspire-standalone"], nativeMapping.GetConfiguredPackageSources("Aspire.Hosting.Redis"));
        Assert.Equal(["aspire-standalone"], nativeMapping.GetConfiguredPackageSources("aspire.hosting.redis"));
        Assert.Equal(["aspire-standalone"], nativeMapping.GetConfiguredPackageSources(packageId));
        Assert.Equal(["aspire-standalone", "aspire-standalone-0"], nativeMapping.GetConfiguredPackageSources("Example.Dependency"));
    }

    [Theory]
    [InlineData("https://example.test/Feed/v3/index.json")]
    [InlineData("https://example.test/feed/v3/index.json/")]
    [InlineData("http://example.test/feed/v3/index.json")]
    public async Task Create_DistinctUriLocationsRemainEligible(string channelSource)
    {
        const string source = "https://example.test/feed/v3/index.json";
        const string packageId = "CommunityToolkit.Aspire.Hosting.Redis";
        var channel = CreateSourceIsolatedChannel(channelSource, "Aspire*");

        var mappings = PackageSourceOverrideMappings.Create(source, channel, nugetServiceIndexOverride: null, packageId);

        Assert.Equal(
        [
            (packageId, source),
            ("Aspire*", source),
            (PackageMapping.AllPackages, source),
            ("Aspire*", channelSource),
            (PackageMapping.AllPackages, PackageSources.NuGetOrg)
        ],
            mappings.Select(mapping => (mapping.PackageFilter, mapping.Source)));

        using var config = await NuGetTestHelper.CreateStandaloneConfigurationAsync(mappings);
        var settings = Settings.LoadSpecificSettings(config.ConfigFile.DirectoryName!, config.ConfigFile.Name);
        var nativeMapping = PackageSourceMapping.GetPackageSourceMapping(settings);

        Assert.Equal(["aspire-standalone", "aspire-standalone-0"], nativeMapping.GetConfiguredPackageSources("Aspire.Hosting.Redis"));
        Assert.Equal(["aspire-standalone"], nativeMapping.GetConfiguredPackageSources(packageId));
    }

    [Fact]
    public async Task Create_LocalPathAndFileUriPreserveNativeEligibilityAndCacheIdentity()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var source = workspace.CreateDirectory("Feed With Spaces").FullName;
        var fileUri = new Uri(source).AbsoluteUri;
        const string packageId = "CommunityToolkit.Aspire.Hosting.Redis";
        var channel = CreateSourceIsolatedChannel(fileUri, "Aspire*");

        var mappings = PackageSourceOverrideMappings.Create(source, channel, nugetServiceIndexOverride: null, packageId);
        var fileUriMappings = PackageSourceOverrideMappings.Create(fileUri, channel, nugetServiceIndexOverride: null, packageId);

        Assert.Equal(
        [
            (packageId, source),
            ("Aspire*", source),
            (PackageMapping.AllPackages, source),
            (PackageMapping.AllPackages, PackageSources.NuGetOrg)
        ],
            mappings.Select(mapping => (mapping.PackageFilter, mapping.Source)));
        Assert.Equal(
            IntegrationRestorePlanResolver.CreateGlobalPackagesFolderIdentity([fileUri], fileUriMappings),
            IntegrationRestorePlanResolver.CreateGlobalPackagesFolderIdentity([source], mappings));

        using var config = await NuGetTestHelper.CreateStandaloneConfigurationAsync(mappings);
        var settings = Settings.LoadSpecificSettings(config.ConfigFile.DirectoryName!, config.ConfigFile.Name);
        var nativeMapping = PackageSourceMapping.GetPackageSourceMapping(settings);

        Assert.Equal(["aspire-standalone"], nativeMapping.GetConfiguredPackageSources("Aspire.Hosting.Redis"));
        Assert.Equal(["aspire-standalone"], nativeMapping.GetConfiguredPackageSources(packageId));
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.AnyUnix)]
    public void ResolveForWorkingDirectory_RelativePathContainingColon_ResolvesAgainstWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var result = PackageSourceOverrideMappings.ResolveForWorkingDirectory("relative:feed", workspace.WorkspaceRoot);

        Assert.Equal(Path.Combine(workspace.WorkspaceRoot.FullName, "relative:feed"), result);
    }

    [Theory]
    [InlineData("C:/feed")]
    [InlineData("a:/feed")]
    [PlatformSpecific(TestPlatforms.AnyUnix)]
    public void ResolveForWorkingDirectory_DosShapedRelativePath_ResolvesAgainstWorkingDirectory(string source)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var result = PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, workspace.WorkspaceRoot);

        Assert.Equal(Path.Combine(workspace.WorkspaceRoot.FullName, source), result);
    }

    [Fact]
    public void ResolveForWorkingDirectory_FileUri_ReturnsUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = "file:///tmp/feed";

        var result = PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, workspace.WorkspaceRoot);

        Assert.Equal(source, result);
    }

    [Fact]
    public void ResolveForWorkingDirectory_MalformedHttpSource_ReturnsUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = "https://user:p#word@packages.example.com/v3/index.json";

        var result = PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, workspace.WorkspaceRoot);

        Assert.Equal(source, result);
        Assert.True(PackageSourceOverrideMappings.HasCredentialMaterial(result));
        Assert.Null(PackageSourceOverrideMappings.GetMissingLocalDirectory(result));
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.Windows)]
    public void ResolveForWorkingDirectory_WindowsFullyQualifiedPath_ReturnsUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = @"C:\feed";

        var result = PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, workspace.WorkspaceRoot);

        Assert.Equal(source, result);
    }

    private static PackageChannel CreateSourceIsolatedChannel(string source, string pattern)
        => PackageChannel.CreateExplicitChannel(
            "staging",
            PackageChannelQuality.Stable,
            [new PackageMapping(pattern, source), new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            NullLogger.Instance,
            configureGlobalPackagesFolder: true);
}
