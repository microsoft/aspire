// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting.Blazor.Tests;

public class EndpointsManifestTransformerTests : IDisposable
{
    private readonly string _tempDir;

    public EndpointsManifestTransformerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BlazorHostingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PrefixEndpointsAssetFile_PrefixesAssetPaths()
    {
        var manifest = new EndpointsManifest
        {
            Endpoints =
            [
                new EndpointEntry { Route = "css/app.css", AssetFile = "css/app.css" },
                new EndpointEntry { Route = "_framework/blazor.webassembly.js", AssetFile = "_framework/blazor.webassembly.js" }
            ]
        };

        var manifestPath = Path.Combine(_tempDir, "endpoints.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.EndpointsManifest));

        var result = await EndpointsManifestTransformer.PrefixEndpointsAssetFileAsync(manifestPath, "store", CancellationToken.None);

        var transformed = JsonSerializer.Deserialize(result, ManifestJsonContext.Default.EndpointsManifest)!;
        Assert.All(transformed.Endpoints, ep => Assert.StartsWith("store/", ep.AssetFile));
    }

    [Fact]
    public async Task PrefixEndpointsAssetFile_AddsFallbackEndpoint_ForIdentityIndexHtml()
    {
        var manifest = new EndpointsManifest
        {
            Endpoints =
            [
                new EndpointEntry
                {
                    Route = "index.html",
                    AssetFile = "index.html",
                    ResponseHeaders = [new EndpointResponseHeader { Name = "Cache-Control", Value = "max-age=3600" }]
                }
            ]
        };

        var manifestPath = Path.Combine(_tempDir, "endpoints.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.EndpointsManifest));

        var result = await EndpointsManifestTransformer.PrefixEndpointsAssetFileAsync(manifestPath, "store", CancellationToken.None);

        var transformed = JsonSerializer.Deserialize(result, ManifestJsonContext.Default.EndpointsManifest)!;

        // Should have original + fallback
        Assert.Equal(2, transformed.Endpoints.Length);

        var fallback = transformed.Endpoints.Single(ep => ep.Route == "{**path:nonfile}");
        Assert.Equal("store/index.html", fallback.AssetFile);
        Assert.Contains(fallback.ResponseHeaders!, h => h.Name == "Cache-Control" && h.Value == "no-store");
    }

    [Fact]
    public async Task PrefixEndpointsAssetFile_SkipsFallback_ForCompressedIndexHtml()
    {
        var manifest = new EndpointsManifest
        {
            Endpoints =
            [
                new EndpointEntry
                {
                    Route = "index.html",
                    AssetFile = "index.html",
                    Selectors = [new EndpointSelector { Name = "Content-Encoding" }]
                }
            ]
        };

        var manifestPath = Path.Combine(_tempDir, "endpoints.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.EndpointsManifest));

        var result = await EndpointsManifestTransformer.PrefixEndpointsAssetFileAsync(manifestPath, "store", CancellationToken.None);

        var transformed = JsonSerializer.Deserialize(result, ManifestJsonContext.Default.EndpointsManifest)!;

        // Should have only the original (no fallback for compressed variant)
        Assert.Single(transformed.Endpoints);
        Assert.Equal("index.html", transformed.Endpoints[0].Route);
    }

    [Fact]
    public async Task PrefixEndpointsAssetFile_PreservesRoutes_Unchanged()
    {
        var manifest = new EndpointsManifest
        {
            Endpoints =
            [
                new EndpointEntry { Route = "css/app.css", AssetFile = "css/app.css" },
                new EndpointEntry { Route = "_framework/blazor.webassembly.js", AssetFile = "_framework/blazor.webassembly.js" }
            ]
        };

        var manifestPath = Path.Combine(_tempDir, "endpoints.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.EndpointsManifest));

        var result = await EndpointsManifestTransformer.PrefixEndpointsAssetFileAsync(manifestPath, "store", CancellationToken.None);

        var transformed = JsonSerializer.Deserialize(result, ManifestJsonContext.Default.EndpointsManifest)!;

        // Routes should NOT be prefixed (MapGroup handles URL prefixing)
        Assert.Contains(transformed.Endpoints, ep => ep.Route == "css/app.css");
        Assert.Contains(transformed.Endpoints, ep => ep.Route == "_framework/blazor.webassembly.js");
    }

    [Fact]
    public async Task MergeRuntimeManifests_MergesMultipleApps()
    {
        var app1Manifest = new DevelopmentManifest
        {
            ContentRoots = ["/app1/wwwroot"],
            Root = new AssetNode
            {
                Children = new Dictionary<string, AssetNode>
                {
                    ["css"] = new AssetNode
                    {
                        Children = new Dictionary<string, AssetNode>
                        {
                            ["app.css"] = new AssetNode
                            {
                                Asset = new AssetMatch { ContentRootIndex = 0 }
                            }
                        }
                    }
                }
            }
        };

        var app2Manifest = new DevelopmentManifest
        {
            ContentRoots = ["/app2/wwwroot"],
            Root = new AssetNode
            {
                Children = new Dictionary<string, AssetNode>
                {
                    ["js"] = new AssetNode
                    {
                        Children = new Dictionary<string, AssetNode>
                        {
                            ["app.js"] = new AssetNode
                            {
                                Asset = new AssetMatch { ContentRootIndex = 0 }
                            }
                        }
                    }
                }
            }
        };

        var app1Path = Path.Combine(_tempDir, "app1.runtime.json");
        var app2Path = Path.Combine(_tempDir, "app2.runtime.json");
        await File.WriteAllTextAsync(app1Path, JsonSerializer.Serialize(app1Manifest, ManifestJsonContext.Default.DevelopmentManifest));
        await File.WriteAllTextAsync(app2Path, JsonSerializer.Serialize(app2Manifest, ManifestJsonContext.Default.DevelopmentManifest));

        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create();
        var storeApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");
        var adminApp = builder.AddBlazorWasmApp("admin", "Admin/Admin.csproj");

        var manifests = new List<AppManifestPaths>
        {
            new(new GatewayAppRegistration(storeApp, "store", []), app1Path, app1Path),
            new(new GatewayAppRegistration(adminApp, "admin", []), app2Path, app2Path)
        };

        var outputPath = Path.Combine(_tempDir, "merged.json");
        await EndpointsManifestTransformer.MergeRuntimeManifestsAsync(
            manifests, outputPath, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

        Assert.True(File.Exists(outputPath));

        var merged = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(outputPath),
            ManifestJsonContext.Default.DevelopmentManifest)!;

        // Should have combined content roots
        Assert.Equal(2, merged.ContentRoots.Length);
        Assert.Equal("/app1/wwwroot", merged.ContentRoots[0]);
        Assert.Equal("/app2/wwwroot", merged.ContentRoots[1]);

        // Should have children keyed by path prefix
        Assert.NotNull(merged.Root.Children);
        Assert.True(merged.Root.Children.ContainsKey("store"));
        Assert.True(merged.Root.Children.ContainsKey("admin"));
    }

    [Fact]
    public async Task MergeRuntimeManifests_OffsetsContentRootIndices()
    {
        var app1Manifest = new DevelopmentManifest
        {
            ContentRoots = ["/app1/root1", "/app1/root2"],
            Root = new AssetNode
            {
                Children = new Dictionary<string, AssetNode>
                {
                    ["file1"] = new AssetNode { Asset = new AssetMatch { ContentRootIndex = 0 } },
                    ["file2"] = new AssetNode { Asset = new AssetMatch { ContentRootIndex = 1 } }
                }
            }
        };

        var app2Manifest = new DevelopmentManifest
        {
            ContentRoots = ["/app2/root1"],
            Root = new AssetNode
            {
                Children = new Dictionary<string, AssetNode>
                {
                    ["file3"] = new AssetNode { Asset = new AssetMatch { ContentRootIndex = 0 } }
                }
            }
        };

        var app1Path = Path.Combine(_tempDir, "app1.runtime.json");
        var app2Path = Path.Combine(_tempDir, "app2.runtime.json");
        await File.WriteAllTextAsync(app1Path, JsonSerializer.Serialize(app1Manifest, ManifestJsonContext.Default.DevelopmentManifest));
        await File.WriteAllTextAsync(app2Path, JsonSerializer.Serialize(app2Manifest, ManifestJsonContext.Default.DevelopmentManifest));

        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create();
        var storeApp = builder.AddBlazorWasmApp("store", "Store/Store.csproj");
        var adminApp = builder.AddBlazorWasmApp("admin", "Admin/Admin.csproj");

        var manifests = new List<AppManifestPaths>
        {
            new(new GatewayAppRegistration(storeApp, "store", []), app1Path, app1Path),
            new(new GatewayAppRegistration(adminApp, "admin", []), app2Path, app2Path)
        };

        var outputPath = Path.Combine(_tempDir, "merged.json");
        await EndpointsManifestTransformer.MergeRuntimeManifestsAsync(
            manifests, outputPath, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

        var merged = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(outputPath),
            ManifestJsonContext.Default.DevelopmentManifest)!;

        // App2's content root index 0 should be offset to 2 (app1 had 2 roots)
        Assert.Equal(3, merged.ContentRoots.Length);
        var adminChildren = merged.Root.Children!["admin"].Children!;
        Assert.Equal(2, adminChildren["file3"].Asset!.ContentRootIndex); // 0 + offset of 2
    }

    [Fact]
    public void AssetNode_OffsetContentRootIndices_OffsetsRecursively()
    {
        var node = new AssetNode
        {
            Asset = new AssetMatch { ContentRootIndex = 0 },
            Patterns = [new AssetPattern { ContentRootIndex = 1 }],
            Children = new Dictionary<string, AssetNode>
            {
                ["child"] = new AssetNode
                {
                    Asset = new AssetMatch { ContentRootIndex = 2 }
                }
            }
        };

        node.OffsetContentRootIndices(5);

        Assert.Equal(5, node.Asset.ContentRootIndex);
        Assert.Equal(6, node.Patterns[0].ContentRootIndex);
        Assert.Equal(7, node.Children["child"].Asset!.ContentRootIndex);
    }
}
