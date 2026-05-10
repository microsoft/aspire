// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.IO.Compression;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace Aspire.Cli.Tests.NuGet;

public sealed class PackageTagMetadataServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task HasTagAsync_ImplicitChannelReadsRelativeLocalSourcePackageMetadata()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");
        var nuGetConfigFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));

        await CreatePackageAsync(packagesDirectory, "Contoso.Hosting.MongoDb", "1.2.3", $"database {HostingIntegrationMetadata.CanonicalTag}");
        await File.WriteAllTextAsync(
            nuGetConfigFile.FullName,
            """
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="packages" />
              </packageSources>
            </configuration>
            """);

        var runner = new TestDotNetCliRunner
        {
            GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [nuGetConfigFile.FullName])
        };

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var handler = new MockHttpMessageHandler(new InvalidOperationException("Unexpected HTTP request."));

        var service = new PackageTagMetadataService(
            runner,
            new MockHttpClientFactory(handler),
            cache,
            NullLogger<PackageTagMetadataService>.Instance);

        var hasTag = await service.HasTagAsync(
            PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache()),
            workspace.WorkspaceRoot,
            "Contoso.Hosting.MongoDb",
            "1.2.3",
            HostingIntegrationMetadata.CanonicalTag,
            CancellationToken.None).DefaultTimeout();

        Assert.True(hasTag);
    }

    [Fact]
    public async Task HasAnyDependencyAsync_ImplicitChannelReadsRelativeLocalSourcePackageMetadata()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");
        var nuGetConfigFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));

        await CreatePackageAsync(packagesDirectory, "Contoso.Hosting.MongoDb", "1.2.3", "database", "Aspire.Hosting.AppHost");
        await File.WriteAllTextAsync(
            nuGetConfigFile.FullName,
            """
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="packages" />
              </packageSources>
            </configuration>
            """);

        var runner = new TestDotNetCliRunner
        {
            GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [nuGetConfigFile.FullName])
        };

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var handler = new MockHttpMessageHandler(new InvalidOperationException("Unexpected HTTP request."));

        var service = new PackageTagMetadataService(
            runner,
            new MockHttpClientFactory(handler),
            cache,
            NullLogger<PackageTagMetadataService>.Instance);

        var hasDependency = await service.HasAnyDependencyAsync(
            PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache()),
            workspace.WorkspaceRoot,
            "Contoso.Hosting.MongoDb",
            "1.2.3",
            HostingIntegrationMetadata.HostingDependencyPackageIds,
            CancellationToken.None).DefaultTimeout();

        Assert.True(hasDependency);
    }

    [Fact]
    public async Task HasTagAsync_ExplicitChannelFollowsPagedRegistrationItems()
    {
        const string source = "https://example.test/v3/index.json";
        const string page = "https://example.test/v3/registration-semver2/contoso.hosting.mongodb/page/1.json";

        var requestedUris = new ConcurrentBag<string>();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var handler = new MockHttpMessageHandler(request =>
        {
            requestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);

            return request.RequestUri?.AbsoluteUri switch
            {
                source => CreateJsonResponse("""
                    {
                      "resources": [
                        {
                          "@id": "https://example.test/v3/registration-semver2/",
                          "@type": "RegistrationsBaseUrl/Versioned"
                        }
                      ]
                    }
                    """),
                "https://example.test/v3/registration-semver2/contoso.hosting.mongodb/index.json" => CreateJsonResponse($$"""
                    {
                      "items": [
                        {
                          "@id": "{{page}}"
                        }
                      ]
                    }
                    """),
                page => CreateJsonResponse("""
                    {
                      "items": [
                        {
                          "catalogEntry": {
                            "version": "1.2.3",
                            "tags": [
                              "database",
                              "aspire-hosting",
                              "aspire"
                            ]
                          }
                        }
                      ]
                    }
                    """),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            };
        });

        var service = new PackageTagMetadataService(
            new TestDotNetCliRunner(),
            new MockHttpClientFactory(handler),
            cache,
            NullLogger<PackageTagMetadataService>.Instance);

        var channel = PackageChannel.CreateExplicitChannel(
            "daily",
            PackageChannelQuality.Both,
            [new PackageMapping("Contoso.Hosting.*", source)],
            new FakeNuGetPackageCache());

        var hasTag = await service.HasTagAsync(
            channel,
            workspace.WorkspaceRoot,
            "Contoso.Hosting.MongoDb",
            "1.2.3",
            HostingIntegrationMetadata.CanonicalTag,
            CancellationToken.None).DefaultTimeout();

        Assert.True(hasTag);
        Assert.Contains(page, requestedUris);
    }

    [Fact]
    public async Task HasAnyDependencyAsync_ExplicitChannelFollowsPagedRegistrationItems()
    {
        const string source = "https://example.test/v3/index.json";
        const string page = "https://example.test/v3/registration-semver2/contoso.hosting.mongodb/page/1.json";

        var requestedUris = new ConcurrentBag<string>();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var handler = new MockHttpMessageHandler(request =>
        {
            requestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);

            return request.RequestUri?.AbsoluteUri switch
            {
                source => CreateJsonResponse("""
                    {
                      "resources": [
                        {
                          "@id": "https://example.test/v3/registration-semver2/",
                          "@type": "RegistrationsBaseUrl/Versioned"
                        }
                      ]
                    }
                    """),
                "https://example.test/v3/registration-semver2/contoso.hosting.mongodb/index.json" => CreateJsonResponse($$"""
                    {
                      "items": [
                        {
                          "@id": "{{page}}"
                        }
                      ]
                    }
                    """),
                page => CreateJsonResponse("""
                    {
                      "items": [
                        {
                          "catalogEntry": {
                            "version": "1.2.3",
                            "dependencyGroups": [
                              {
                                "targetFramework": "net10.0",
                                "dependencies": [
                                  { "id": "Aspire.Hosting.AppHost", "range": "[9.0.0, )" },
                                  { "id": "Microsoft.Extensions.Http", "range": "[9.0.0, )" }
                                ]
                              }
                            ]
                          }
                        }
                      ]
                    }
                    """),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            };
        });

        var service = new PackageTagMetadataService(
            new TestDotNetCliRunner(),
            new MockHttpClientFactory(handler),
            cache,
            NullLogger<PackageTagMetadataService>.Instance);

        var channel = PackageChannel.CreateExplicitChannel(
            "daily",
            PackageChannelQuality.Both,
            [new PackageMapping("Contoso.Hosting.*", source)],
            new FakeNuGetPackageCache());

        var hasDependency = await service.HasAnyDependencyAsync(
            channel,
            workspace.WorkspaceRoot,
            "Contoso.Hosting.MongoDb",
            "1.2.3",
            HostingIntegrationMetadata.HostingDependencyPackageIds,
            CancellationToken.None).DefaultTimeout();

        Assert.True(hasDependency);
        Assert.Contains(page, requestedUris);
    }

    [Fact]
    public async Task HasTagAsync_SetsAbsoluteExpirationOnCacheEntries()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");

        await CreatePackageAsync(packagesDirectory, "Contoso.Hosting.MongoDb", "1.2.3", $"database {HostingIntegrationMetadata.CanonicalTag}");

        using var cache = new TrackingMemoryCache();
        using var handler = new MockHttpMessageHandler(new InvalidOperationException("Unexpected HTTP request."));

        var service = new PackageTagMetadataService(
            new TestDotNetCliRunner(),
            new MockHttpClientFactory(handler),
            cache,
            NullLogger<PackageTagMetadataService>.Instance);

        var channel = PackageChannel.CreateExplicitChannel(
            "daily",
            PackageChannelQuality.Both,
            [new PackageMapping("Contoso.Hosting.*", packagesDirectory.FullName)],
            new FakeNuGetPackageCache());

        var hasTag = await service.HasTagAsync(
            channel,
            workspace.WorkspaceRoot,
            "Contoso.Hosting.MongoDb",
            "1.2.3",
            HostingIntegrationMetadata.CanonicalTag,
            CancellationToken.None).DefaultTimeout();

        Assert.True(hasTag);
        Assert.Equal(TimeSpan.FromHours(1), cache.LastAbsoluteExpirationRelativeToNow);
    }

    [Fact]
    public async Task HasTagAsync_ImplicitChannelCachesResolvedSources()
    {
        var nuGetConfigPathCallCount = 0;

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagesDirectory = workspace.CreateDirectory("packages");
        var nuGetConfigFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config"));

        await CreatePackageAsync(packagesDirectory, "Contoso.Hosting.MongoDb", "1.2.3", $"database {HostingIntegrationMetadata.CanonicalTag}");
        await CreatePackageAsync(packagesDirectory, "Fabrikam.Hosting.Postgres", "2.0.0", $"database {HostingIntegrationMetadata.CanonicalTag}");
        await File.WriteAllTextAsync(
            nuGetConfigFile.FullName,
            """
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="packages" />
              </packageSources>
            </configuration>
            """);

        var runner = new TestDotNetCliRunner
        {
            GetNuGetConfigPathsAsyncCallback = (_, _, _) =>
            {
                nuGetConfigPathCallCount++;
                return (0, [nuGetConfigFile.FullName]);
            }
        };

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var handler = new MockHttpMessageHandler(new InvalidOperationException("Unexpected HTTP request."));

        var service = new PackageTagMetadataService(
            runner,
            new MockHttpClientFactory(handler),
            cache,
            NullLogger<PackageTagMetadataService>.Instance);

        var channel = PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache());

        var mongoHasTag = await service.HasTagAsync(
            channel,
            workspace.WorkspaceRoot,
            "Contoso.Hosting.MongoDb",
            "1.2.3",
            HostingIntegrationMetadata.CanonicalTag,
            CancellationToken.None).DefaultTimeout();
        var postgresHasTag = await service.HasTagAsync(
            channel,
            workspace.WorkspaceRoot,
            "Fabrikam.Hosting.Postgres",
            "2.0.0",
            HostingIntegrationMetadata.CanonicalTag,
            CancellationToken.None).DefaultTimeout();

        Assert.True(mongoHasTag);
        Assert.True(postgresHasTag);
        Assert.Equal(1, nuGetConfigPathCallCount);
    }

    [Fact]
    public async Task HasTagAsync_RemoteChannelCachesRegistrationBaseUrl()
    {
        const string source = "https://example.test/v3/index.json";

        var requestedUris = new ConcurrentBag<string>();

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var handler = new MockHttpMessageHandler(request =>
        {
            requestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);

            return request.RequestUri?.AbsoluteUri switch
            {
                source => CreateJsonResponse("""
                    {
                      "resources": [
                        {
                          "@id": "https://example.test/v3/registration-semver2/",
                          "@type": "RegistrationsBaseUrl/Versioned"
                        }
                      ]
                    }
                    """),
                "https://example.test/v3/registration-semver2/contoso.hosting.mongodb/index.json" => CreateJsonResponse($$"""
                    {
                      "items": [
                        {
                          "catalogEntry": {
                            "version": "1.2.3",
                            "tags": [
                              "{{HostingIntegrationMetadata.CanonicalTag}}"
                            ]
                          }
                        }
                      ]
                    }
                    """),
                "https://example.test/v3/registration-semver2/fabrikam.hosting.postgres/index.json" => CreateJsonResponse($$"""
                    {
                      "items": [
                        {
                          "catalogEntry": {
                            "version": "2.0.0",
                            "tags": [
                              "{{HostingIntegrationMetadata.CanonicalTag}}"
                            ]
                          }
                        }
                      ]
                    }
                    """),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            };
        });

        var service = new PackageTagMetadataService(
            new TestDotNetCliRunner(),
            new MockHttpClientFactory(handler),
            cache,
            NullLogger<PackageTagMetadataService>.Instance);

        var channel = PackageChannel.CreateExplicitChannel(
            "daily",
            PackageChannelQuality.Both,
            [new PackageMapping("*", source)],
            new FakeNuGetPackageCache());

        var mongoHasTag = await service.HasTagAsync(
            channel,
            workspace.WorkspaceRoot,
            "Contoso.Hosting.MongoDb",
            "1.2.3",
            HostingIntegrationMetadata.CanonicalTag,
            CancellationToken.None).DefaultTimeout();
        var postgresHasTag = await service.HasTagAsync(
            channel,
            workspace.WorkspaceRoot,
            "Fabrikam.Hosting.Postgres",
            "2.0.0",
            HostingIntegrationMetadata.CanonicalTag,
            CancellationToken.None).DefaultTimeout();

        Assert.True(mongoHasTag);
        Assert.True(postgresHasTag);
        Assert.Equal(1, requestedUris.Count(uri => uri == source));
    }

    private static async Task CreatePackageAsync(DirectoryInfo packageDirectory, string packageId, string version, string tags, params string[] dependencies)
    {
        var packagePath = Path.Combine(packageDirectory.FullName, $"{packageId}.{version}.nupkg");

        var dependencyItems = string.Join(
            Environment.NewLine,
            dependencies.Select(static dependency => $"                    <dependency id=\"{dependency}\" version=\"[9.0.0, )\" />"));
        var dependencyXml = dependencies.Length == 0
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                string.Empty,
                "                <dependencies>",
                "                  <group targetFramework=\"net10.0\">",
                dependencyItems,
                "                  </group>",
                "                </dependencies>");

        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        var nuspecEntry = archive.CreateEntry($"{packageId}.nuspec");

        await using var stream = nuspecEntry.Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync($$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{{packageId}}</id>
                <version>{{version}}</version>
                <authors>Test</authors>
                <description>Test package</description>
                <tags>{{tags}}</tags>
            {{dependencyXml}}
              </metadata>
            </package>
            """);
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class TrackingMemoryCache : IMemoryCache
    {
        private readonly Dictionary<object, object?> _entries = [];

        public TimeSpan? LastAbsoluteExpirationRelativeToNow { get; private set; }

        public bool TryGetValue(object key, out object? value) => _entries.TryGetValue(key, out value);

        public ICacheEntry CreateEntry(object key) => new TrackingCacheEntry(key, this);

        public void Remove(object key) => _entries.Remove(key);

        public void Dispose()
        {
        }

        private sealed class TrackingCacheEntry(object key, TrackingMemoryCache owner) : ICacheEntry
        {
            public object Key { get; } = key;

            public object? Value { get; set; }

            public DateTimeOffset? AbsoluteExpiration { get; set; }

            public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }

            public TimeSpan? SlidingExpiration { get; set; }

            public IList<IChangeToken> ExpirationTokens { get; } = [];

            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; } = [];

            public CacheItemPriority Priority { get; set; }

            public long? Size { get; set; }

            public void Dispose()
            {
                owner.LastAbsoluteExpirationRelativeToNow = AbsoluteExpirationRelativeToNow;
                owner._entries[Key] = Value;
            }
        }
    }
}
