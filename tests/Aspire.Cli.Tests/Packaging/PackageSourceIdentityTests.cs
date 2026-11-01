// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using NuGet.Configuration;

namespace Aspire.Cli.Tests.Packaging;

public class PackageSourceIdentityTests(ITestOutputHelper outputHelper)
{
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

    private static void WriteSource(DirectoryInfo directory, string source)
        => new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add",
                        new XAttribute("key", "local"),
                        new XAttribute("value", source)))))
            .Save(Path.Combine(directory.FullName, "NuGet.Config"));
}
