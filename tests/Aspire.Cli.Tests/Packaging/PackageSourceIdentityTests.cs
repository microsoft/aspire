// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using System.Globalization;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using NuGet.Configuration;

namespace Aspire.Cli.Tests.Packaging;

public class PackageSourceIdentityTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("team")]
    [InlineData("Team_1")]
    [InlineData("1team")]
    [InlineData("team.source")]
    [InlineData("nugetOrg")]
    [InlineData("daily")]
    public void ExplicitSourceKeyIsPrefixedForNewFeeds(string sourceKey)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSource(workspace.WorkspaceRoot, "https://example.com/existing");
        var snapshot = NuGetTestHelper.CreateClient().GetSettings(
            workspace.Path, new byte[NuGetSourceIdentity.KeySizeInBytes]);

        var aliases = NuGetConfigurationBuilder.ResolveSourceAliases(
            [new PackageMapping("Contoso.Hosting.One", "https://example.com/team") { SourceKey = sourceKey }],
            "integration-add", snapshot.Sources, snapshot.ReservedPackageSourceKeys, snapshot.SourceIdentityKey);

        var alias = Assert.Single(aliases);
        Assert.Equal($"aspire-integration-{sourceKey}", alias.Key);
        Assert.False(alias.IsAmbient);
        Assert.True(alias.IsExplicitlyNamed);
        Assert.True(NuGetConfigurationBuilder.IsCliManagedSourceKey(alias.Key, "different-workload"));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("aspire-integration-add")]
    [InlineData("aspire-integration-team")]
    [InlineData("team")]
    [InlineData("nuget.org")]
    [InlineData("team.source")]
    public void ExplicitSourceKeyPreservesExistingFeedAliases(string existingKey)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        WriteSource(workspace.WorkspaceRoot, "https://example.com/team", existingKey);
        var snapshot = NuGetTestHelper.CreateClient().GetSettings(
            workspace.Path, new byte[NuGetSourceIdentity.KeySizeInBytes]);

        var aliases = NuGetConfigurationBuilder.ResolveSourceAliases(
            [new PackageMapping("Contoso.Hosting.One", "https://example.com/team") { SourceKey = "team" }],
            "integration-add", snapshot.Sources, snapshot.ReservedPackageSourceKeys, snapshot.SourceIdentityKey);

        var alias = Assert.Single(aliases);
        Assert.Equal(existingKey, alias.Key);
        Assert.True(alias.IsAmbient);
        Assert.Equal(existingKey is "team" or "aspire-integration-team", alias.IsExplicitlyNamed);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("LOCAL")]
    [InlineData("reserved")]
    public void ExplicitSourceKeyRejectsCollisionsWithoutOverwriting(string sourceKey)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var reservedKey = $"aspire-integration-{sourceKey}";
        WriteSource(workspace.WorkspaceRoot, "https://example.com/existing", reservedKey);
        var snapshot = NuGetTestHelper.CreateClient().GetSettings(
            workspace.Path, new byte[NuGetSourceIdentity.KeySizeInBytes]);

        var exception = Assert.Throws<InvalidOperationException>(() => NuGetConfigurationBuilder.ResolveSourceAliases(
            [new PackageMapping("Contoso.Hosting.One", "https://example.com/team") { SourceKey = sourceKey }],
            "integration-add", snapshot.Sources, snapshot.ReservedPackageSourceKeys, snapshot.SourceIdentityKey));

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, ErrorStrings.NuGetSourceKeyAlreadyInUse, reservedKey), exception.Message);
        Assert.Equal("https://example.com/existing",
            Assert.Single(new PackageSourceProvider(Settings.LoadSpecificSettings(workspace.Path, "NuGet.Config")).LoadPackageSources()).Source);
    }

    [Fact]
    public void ExplicitSourceKeyReusesExistingAliasWhenGeneratedKeyIsReserved()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = "https://example.com/team";
        WriteSource(workspace.WorkspaceRoot, source, "company-feed");
        var snapshot = NuGetTestHelper.CreateClient().GetSettings(
            workspace.Path, new byte[NuGetSourceIdentity.KeySizeInBytes]);

        var alias = Assert.Single(NuGetConfigurationBuilder.ResolveSourceAliases(
            [new PackageMapping("Contoso.Hosting.One", source) { SourceKey = "team" }],
            "integration-add", snapshot.Sources, [.. snapshot.ReservedPackageSourceKeys, "aspire-integration-team"],
            snapshot.SourceIdentityKey));

        Assert.Equal("company-feed", alias.Key);
        Assert.True(alias.IsAmbient);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalPathAndFileUriReuseNuGetResolvedAlias(bool configuredAsFileUri)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feed = workspace.CreateDirectory("Feed With Spaces");
        var fileUri = new Uri(feed.FullName).AbsoluteUri;
        var configuredSource = configuredAsFileUri
            ? fileUri
            : Path.GetRelativePath(workspace.WorkspaceRoot.FullName, feed.FullName);
        WriteSource(workspace.WorkspaceRoot, configuredSource);

        var nativeSettings = Settings.LoadSpecificSettings(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        var nativeSource = Assert.Single(new PackageSourceProvider(nativeSettings).LoadPackageSources());
        Assert.Equal(
            PackageSourceIdentity.Normalize(feed.FullName),
            PackageSourceIdentity.Normalize(nativeSource.Source));

        var snapshot = NuGetTestHelper.CreateClient().GetSettings(
            workspace.WorkspaceRoot.FullName,
            new byte[NuGetSourceIdentity.KeySizeInBytes]);
        foreach (var selectedSource in new[] { feed.FullName, fileUri })
        {
            var aliases = NuGetConfigurationBuilder.ResolveSourceAliases(
                [new PackageMapping("Aspire*", selectedSource)],
                "identity-test",
                snapshot.Sources,
                snapshot.ReservedPackageSourceKeys,
                snapshot.SourceIdentityKey);

            var alias = Assert.Single(aliases);
            Assert.Equal(nativeSource.Name, alias.Key);
            Assert.True(alias.IsAmbient);
        }
    }

    [Fact]
    public void LocalSourceAliasMatchingUsesPlatformPathCaseRules()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var feed = workspace.CreateDirectory("Feed With Spaces");
        var caseVariant = Path.Combine(feed.Parent!.FullName, "feed with spaces");
        WriteSource(workspace.WorkspaceRoot, feed.FullName);
        var snapshot = NuGetTestHelper.CreateClient().GetSettings(
            workspace.WorkspaceRoot.FullName,
            new byte[NuGetSourceIdentity.KeySizeInBytes]);

        var aliases = NuGetConfigurationBuilder.ResolveSourceAliases(
            [new PackageMapping("Aspire*", caseVariant)],
            "identity-case",
            snapshot.Sources,
            snapshot.ReservedPackageSourceKeys,
            snapshot.SourceIdentityKey);

        var alias = Assert.Single(aliases);
        Assert.Equal(OperatingSystem.IsWindows(), PackageSourceIdentity.Comparer.Equals(feed.FullName, caseVariant));
        Assert.Equal(OperatingSystem.IsWindows(), alias.IsAmbient);
        Assert.Equal(OperatingSystem.IsWindows() ? "local" : "aspire-identity-case", alias.Key);
    }

    private static void WriteSource(DirectoryInfo directory, string source, string key = "local")
        => new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add",
                        new XAttribute("key", key),
                        new XAttribute("value", source)))))
            .Save(Path.Combine(directory.FullName, "NuGet.Config"));
}
