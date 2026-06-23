// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Integrations;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Integrations;

public class IntegrationIndexSourceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task NuGetSearchIndexSourceProjectsNuGetPackagesAsIntegrationCandidates()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) =>
            {
                return Task.FromResult<IEnumerable<NuGetPackage>>([
                    CreatePackage("Aspire.Hosting.Redis", "13.4.0"),
                    CreatePackage("CommunityToolkit.Aspire.Hosting.Cosmos", "13.4.0"),
                ]);
            }
        };
        var channel = PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance);
        var source = new NuGetSearchIntegrationIndexSource();

        var candidates = (await source.GetPackageCandidatesAsync(
            new IntegrationIndexSourceContext(workspace.WorkspaceRoot, [channel]),
            CancellationToken.None)).OrderBy(static candidate => candidate.Name).ToArray();

        Assert.Equal(IntegrationIndexSourceKind.DynamicNuGetSearch, source.Index.SourceKind);
        Assert.Equal("nuget-search", source.Index.Id);
        Assert.Collection(candidates,
            candidate =>
            {
                Assert.Equal("communitytoolkit-cosmos", candidate.Name);
                Assert.Equal("CommunityToolkit.Aspire.Hosting.Cosmos", candidate.Provider.Package);
                Assert.Equal("nuget:CommunityToolkit.Aspire.Hosting.Cosmos", candidate.ProviderCoordinate);
            },
            candidate =>
            {
                Assert.Equal("redis", candidate.Name);
                Assert.Equal("nuget-search/redis", candidate.QualifiedName);
                Assert.Equal("Aspire.Hosting.Redis", candidate.Package.Id);
                Assert.Equal("Aspire.Hosting.Redis", candidate.Provider.Package);
                Assert.Equal("nuget:Aspire.Hosting.Redis", candidate.ProviderCoordinate);
                Assert.True(candidate.IsExactMatch("redis"));
                Assert.True(candidate.IsExactMatch("nuget-search/redis"));
                Assert.True(candidate.IsExactMatch("Aspire.Hosting.Redis"));
                Assert.True(candidate.IsExactMatch("nuget:Aspire.Hosting.Redis"));
            });
    }

    [Fact]
    public async Task StaticGeneratedIndexSourceResolvesNuGetProviderEntriesThroughChannels()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, packageId, filter, prerelease, _, useCache, _) =>
            {
                Assert.Equal("Aspire.Hosting.Redis", packageId);
                Assert.True(filter?.Invoke("Aspire.Hosting.Redis") == true);
                Assert.True(useCache);

                return Task.FromResult<IEnumerable<NuGetPackage>>(prerelease
                    ? [CreatePackage("Aspire.Hosting.Redis", "13.5.0-preview.1")]
                    : [CreatePackage("Aspire.Hosting.Redis", "13.4.0")]);
            }
        };
        var channel = PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance);
        var index = new IntegrationIndexDescriptor("aspire", "Aspire", "official", IntegrationIndexSourceKind.StaticGeneratedArtifact);
        var source = new StaticGeneratedIntegrationIndexSource([
            new IntegrationEntry
            {
                Index = index,
                Id = "redis",
                DisplayName = "Redis",
                Aliases = ["cache"],
                Providers = [new IntegrationProviderReference(IntegrationProviderTypes.NuGet, "Aspire.Hosting.Redis")]
            }
        ]);

        var candidates = (await source.GetPackageCandidatesAsync(
            new IntegrationIndexSourceContext(workspace.WorkspaceRoot, [channel]),
            CancellationToken.None)).OrderBy(static candidate => candidate.Package.Version).ToArray();

        Assert.Equal(IntegrationIndexSourceKind.StaticGeneratedArtifact, source.Index.SourceKind);
        Assert.Collection(candidates,
            candidate =>
            {
                Assert.Equal("redis", candidate.Name);
                Assert.Equal("aspire/redis", candidate.QualifiedName);
                Assert.Equal("13.4.0", candidate.Package.Version);
                Assert.True(candidate.IsExactMatch("cache"));
                Assert.True(candidate.IsExactMatch("aspire/redis"));
            },
            candidate =>
            {
                Assert.Equal("redis", candidate.Name);
                Assert.Equal("13.5.0-preview.1", candidate.Package.Version);
                Assert.Equal("nuget:Aspire.Hosting.Redis", candidate.ProviderCoordinate);
            });
    }

    private static NuGetPackage CreatePackage(string id, string version)
    {
        return new NuGetPackage
        {
            Id = id,
            Source = "nuget",
            Version = version
        };
    }
}
