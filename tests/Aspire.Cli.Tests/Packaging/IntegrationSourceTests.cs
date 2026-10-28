// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Packaging;

public class IntegrationSourceTests(ITestOutputHelper output)
{
    [Fact]
    public void Resolve_PreservesBuiltInDiscoveryByDefault()
    {
        using var configuration = new ConfigurationManager();
        var source = Assert.Single(IntegrationSourceConfiguration.Resolve(configuration, new DirectoryInfo(Environment.CurrentDirectory)));
        Assert.Equal("default", source.Name);
        Assert.Null(source.Source);
    }

    [Theory]
    [InlineData("""{"integrationSources":"invalid"}""")]
    [InlineData("""{"integrationSources":{"team":"invalid"}}""")]
    [InlineData("""{"integrationSources":{"team":{}}}""")]
    [InlineData("""{"integrationSources":{"team":{"source":"https://example.com/v3/index.json","patterns":[]}}}""")]
    [InlineData("""{"integrationSources":{"team":{"source":"https://example.com/v3/index.json","patterns":["Acme.*.Hosting"]}}}""")]
    [InlineData("""{"integrationSources":{"team":{"source":"https://example.com/v3/index.json","patterns":[""]}}}""")]
    [InlineData("""{"integrationSources":{"team":{"source":"https://example.com/v3/index.json","patterns":{"other":"Acme.*"}}}}""")]
    [InlineData("""{"integrationSources":{"team":{"source":"https://example.com/v3/index.json","patterns":"Acme.*"}}}""")]
    [InlineData("""{"integrationSources":{"team":{"source":"ftp://example.com/feed","patterns":["*"]}}}""")]
    [InlineData("""{"integrationSources":{"team":{"source":"https://user:password@example.com/feed","patterns":["*"]}}}""")]
    public void Resolve_RejectsInvalidConfiguration(string json)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Throws<InvalidOperationException>(() => IntegrationSourceConfiguration.Resolve(configuration, new DirectoryInfo(Environment.CurrentDirectory)));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("team")]
    public void Resolve_OmittedPatternsAllowAllPackagesIncludingWhenMerged(string name)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["integrationSources:default:enabled"] = "false",
            [$"integrationSources:{name}:enabled"] = "true",
            [$"integrationSources:{name}:source"] = "https://example.com/feed",
            ["integrationSources:restricted:source"] = "https://example.com/feed",
            ["integrationSources:restricted:patterns:0"] = "Acme.*"
        });

        var source = Assert.Single(IntegrationSourceConfiguration.Resolve(configuration, new DirectoryInfo(Environment.CurrentDirectory)));

        Assert.Contains("*", source.Patterns!);
        Assert.True(source.AllowsPackage("Other.Hosting.Redis"));
        Assert.Equal("", source.SearchTerm);
    }

    [Theory]
    [InlineData("Acme.Hosting.*", "acme.hosting.Redis", true)]
    [InlineData("Acme.Hosting.Redis", "acme.hosting.redis", true)]
    [InlineData("Acme.Hosting.Redis", "Acme.Hosting.Redis.Extra", false)]
    [InlineData("Acme.Hosting.*", "Other.Hosting.Redis", false)]
    [InlineData("*", "Other.Hosting.Redis", true)]
    public void AllowsPackage_UsesNuGetPatterns(string pattern, string packageId, bool expected)
    {
        var source = new IntegrationSourceConfiguration { Patterns = [pattern] };
        Assert.Equal(expected, source.AllowsPackage(packageId));
    }

    [Fact]
    public void Resolve_ReadsFreshSelectedAppAndMergesGlobalFields()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var app = workspace.CreateDirectory("app");
        var global = new FileInfo(Path.Combine(workspace.Path, "global.json"));
        File.WriteAllText(global.FullName, """
            {"integrationSources":{"team":{"source":"https://example.com/global","patterns":["Acme.*"]}}}
            """);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName), """
            {"integrationSources":{"old":{"source":"https://example.com/old","patterns":["Old.*"]}}}
            """);
        File.WriteAllText(Path.Combine(app.FullName, AspireConfigFile.FileName), """
            {"integrationSources":{"default":{"enabled":false},"TEAM":{"source":"https://example.com/local"}}}
            """);

        var source = Assert.Single(IntegrationSourceConfiguration.Resolve(app, global));

        Assert.Equal("https://example.com/local", source.Source);
        Assert.NotNull(source.Patterns);
        Assert.Equal(["Acme.*"], source.Patterns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_QueriesEachConfiguredFeedAndAppliesPatterns(bool polyglot)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName), """
            {"integrationSources":{
              "default":{"enabled":false},
              "team":{"source":"https://example.com/team","patterns":["Acme.*"]},
              "teamAlias":{"source":"https://EXAMPLE.com/team","patterns":["Exact.Integration","acme.*"]},
              "other":{"source":"https://example.com/other","patterns":["Other.*"]},
              "disabled":{"enabled":false}
            }}
            """);
        var queries = new System.Collections.Concurrent.ConcurrentBag<(string Source, string Query)>();
        var cache = new FakeNuGetPackageCache
        {
            GetPackagesAsyncCallback = (_, query, filter, _, config, _, _) =>
            {
                var source = Assert.Single(XDocument.Load(config!.FullName).Root!.Element("packageSources")!.Elements("add")).Attribute("value")!.Value;
                queries.Add((source, query));
                var packages = source.EndsWith("/team", StringComparison.Ordinal)
                    ? new[] { Package("Acme.Hosting.Redis", source), Package("Exact.Integration", source), Package("Unapproved.Package", source) }
                    : [Package("Other.Hosting.Redis", source), Package("Unapproved.Package", source)];
                return Task.FromResult(packages.Where(package => filter!(package.Id)));
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, output);
        services.AddSingleton<INuGetPackageCache>(cache);
        using var provider = services.BuildServiceProvider();
        var search = provider.GetRequiredService<IntegrationPackageSearchService>();

        IReadOnlyList<(NuGetPackage Package, PackageChannel Channel)> packages;
        if (polyglot)
        {
            var result = await search.GetIntegrationPackagesWithPolyglotCompatibilityAsync(workspace.WorkspaceRoot, null, TestContext.Current.CancellationToken);
            packages = result.Packages;
            Assert.Equal(["Acme.Hosting.Redis", "Exact.Integration", "Other.Hosting.Redis"], result.PolyglotCompatibleIds.Order());
        }
        else
        {
            packages = (await search.GetIntegrationPackagesWithChannelsAsync(workspace.WorkspaceRoot, null, TestContext.Current.CancellationToken)).ToArray();
        }

        Assert.Equal(["Acme.Hosting.Redis", "Exact.Integration", "Other.Hosting.Redis"], packages.Select(p => p.Package.Id).Order());
        Assert.All(packages, p => Assert.Equal(p.Channel.IntegrationSource!.Source, p.Package.Source));
        Assert.Equal(polyglot ? 6 : 2, queries.Count);
        Assert.Equal("", Assert.Single(queries, q => q.Source.EndsWith("/team", StringComparison.Ordinal) && q.Query != "tags:polyglot").Query);
        var mergedSource = Assert.Single(packages.Select(p => p.Channel).Distinct(), channel => channel.IntegrationSource!.Source == "https://example.com/team").IntegrationSource!;
        Assert.Equal(["Acme.*", "Exact.Integration"], mergedSource.Patterns!);
    }

    [Theory]
    [InlineData("Acme.Hosting.*", "acme.Hosting.Redis", "Acme.Hosting.")]
    [InlineData("Acme.*", "Other.*", "")]
    [InlineData("*", "Acme.*", "")]
    [InlineData("Acme.Redis", "Acme.Redis*", "Acme.Redis")]
    public void Resolve_MergesFeedPatternsAndUsesCommonSearchPrefix(string first, string second, string expectedSearch)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["integrationSources:default:enabled"] = "false",
            ["integrationSources:a:source"] = "https://example.com/feed",
            ["integrationSources:a:patterns:0"] = first,
            ["integrationSources:b:source"] = "https://EXAMPLE.com/feed",
            ["integrationSources:b:patterns:0"] = second,
            ["integrationSources:disabled:enabled"] = "false",
            ["integrationSources:disabled:source"] = "https://example.com/feed",
            ["integrationSources:disabled:patterns:0"] = "Excluded.*"
        });

        var source = Assert.Single(IntegrationSourceConfiguration.Resolve(configuration, new DirectoryInfo(Environment.CurrentDirectory)));

        Assert.Equal("a", source.Name);
        Assert.Equal("https://example.com/feed", source.Source);
        Assert.Equal([first, second], source.Patterns!);
        Assert.Equal(expectedSearch, source.SearchTerm);
    }

    [Fact]
    public async Task Discovery_CombinesDefaultAndNamedSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName), """
            {"integrationSources":{"team":{"source":"https://example.com/team","patterns":["Acme.*"]}}}
            """);
        var cache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([Package("Aspire.Hosting.Redis", "default")]),
            GetPackagesAsyncCallback = (_, _, _, _, _, _, _) => Task.FromResult<IEnumerable<NuGetPackage>>([Package("Acme.Hosting.Redis", "https://example.com/team")])
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, output, options =>
        {
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
                    [PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance)])
            };
        });
        services.AddSingleton<INuGetPackageCache>(cache);
        using var provider = services.BuildServiceProvider();

        var packages = await provider.GetRequiredService<IntegrationPackageSearchService>()
            .GetIntegrationPackagesWithChannelsAsync(workspace.WorkspaceRoot, null, TestContext.Current.CancellationToken);

        Assert.Equal(["Acme.Hosting.Redis", "Aspire.Hosting.Redis"], packages.Select(p => p.Package.Id).Order());
    }

    [Fact]
    public async Task Discovery_SourceOverrideIgnoresConfiguredSources()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName), """{"integrationSources":"invalid"}""");
        var cache = new FakeNuGetPackageCache
        {
            GetIntegrationPackagesAsyncCallback = (_, _, config, _) =>
            {
                Assert.Equal("https://example.com/override",
                    Assert.Single(XDocument.Load(config!.FullName).Root!.Element("packageSources")!.Elements("add")).Attribute("value")!.Value);
                return Task.FromResult<IEnumerable<NuGetPackage>>([Package("Aspire.Hosting.Redis", "https://example.com/override")]);
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, output, options =>
        {
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
                    [PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance)])
            };
        });
        using var provider = services.BuildServiceProvider();

        var packages = await provider.GetRequiredService<IntegrationPackageSearchService>()
            .GetIntegrationPackagesWithChannelsAsync(workspace.WorkspaceRoot, null, "https://example.com/override", TestContext.Current.CancellationToken);

        Assert.Equal("Aspire.Hosting.Redis", Assert.Single(packages).Package.Id);
    }

    [Fact]
    public async Task Discovery_LocalFeedIncludesCustomPackagesAndVersionsButNotDisallowedPackages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var feed = workspace.CreateDirectory("feed");
        NuGetTestHelper.CreatePackage(feed, "Acme.Hosting.Redis", "1.0.0", new Dictionary<string, string>());
        NuGetTestHelper.CreatePackage(feed, "Acme.Hosting.Redis", "2.0.0-preview.1", new Dictionary<string, string>());
        NuGetTestHelper.CreatePackage(feed, "Excluded.Package", "1.0.0", new Dictionary<string, string>());
        File.WriteAllText(Path.Combine(workspace.Path, AspireConfigFile.FileName), """
            {"integrationSources":{"default":{"enabled":false},"team":{"source":"./feed","patterns":["Acme.Hosting.*"]},"teamAlias":{"source":"././feed","patterns":["Acme.Hosting.Redis"]}}}
            """);
        var services = CliTestHelper.CreateServiceCollection(workspace, output);
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IntegrationPackageSearchService>()
            .GetIntegrationPackagesWithPolyglotCompatibilityAsync(workspace.WorkspaceRoot, null, TestContext.Current.CancellationToken);

        var package = Assert.Single(result.Packages);
        Assert.Equal("Acme.Hosting.Redis", package.Package.Id);
        Assert.Equal("2.0.0-preview.1", package.Package.Version);
        Assert.Equal(feed.FullName, package.Package.Source);
        Assert.Empty(result.PolyglotCompatibleIds);
        var versions = await package.Channel.GetPackageVersionsAsync(package.Package.Id, workspace.WorkspaceRoot, TestContext.Current.CancellationToken);
        Assert.Equal(["1.0.0", "2.0.0-preview.1"], versions.Select(p => p.Version).Order());
        Assert.Empty(await package.Channel.GetPackageVersionsAsync("Excluded.Package", workspace.WorkspaceRoot, TestContext.Current.CancellationToken));
    }

    private static NuGetPackage Package(string id, string source)
        => new() { Id = id, Version = "1.0.0", Source = source };
}
