// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Publishing;

namespace Aspire.Hosting.Tests.Publishing;

public sealed class ContainerImageArchiveRewriterTests(ITestOutputHelper output)
{
    private const string SourceTag = "aspire-layered-0123456789abcdef";

    [Fact]
    public async Task RewriteDockerMetadataPreservesPayloadAndUnrelatedReferences()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceArchive = Path.Combine(workspace.Path, "source.tar");
        var destinationArchive = Path.Combine(workspace.Path, "destination.tar");
        const string ImageName = "registry.example.com:5443/team/widget";
        const string DestinationTag = "release-2026.09";
        var manifest = $$"""
            [
              {
                "Config": "config.json",
                "RepoTags": [
                  "{{ImageName}}:{{SourceTag}}",
                  "{{ImageName}}:stable",
                  "registry.example.com:5443/team/other:{{SourceTag}}"
                ],
                "Layers": [ "layer.tar" ],
                "x-unknown": {
                  "enabled": true
                }
              }
            ]
            """;
        var repositories = $$"""
            {
              "{{ImageName}}": {
                "{{SourceTag}}": "owned-layer-id",
                "stable": "stable-layer-id"
              },
              "registry.example.com:5443/team/other": {
                "{{SourceTag}}": "unrelated-layer-id"
              }
            }
            """;
        var config = $$"""
            {
              "architecture": "arm64",
              "config": {
                "Labels": {
                  "private-reference": "{{ImageName}}:{{SourceTag}}"
                }
              },
              "os": "linux"
            }
            """;
        var opaqueBlob = $$"""
            {
              "predicate": {
                "image": "{{ImageName}}:{{SourceTag}}"
              }
            }
            """;
        TestContainerImageArchive.WriteArchive(
            sourceArchive,
            "unchanged-layer-payload",
            ("manifest.json", manifest),
            ("repositories", repositories),
            ("config.json", config),
            ("blobs/sha256/attestation", opaqueBlob));
        var sourceEntryNames = TestContainerImageArchive.ReadEntryNames(sourceArchive);

        await ContainerImageArchiveRewriter.RewriteImageTagAsync(
            sourceArchive,
            destinationArchive,
            ImageName,
            SourceTag,
            DestinationTag,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                $"{ImageName}:{DestinationTag}",
                $"{ImageName}:stable",
                $"registry.example.com:5443/team/other:{SourceTag}"
            ],
            TestContainerImageArchive.ReadDockerImageReferences(destinationArchive));
        Assert.Equal("unchanged-layer-payload", TestContainerImageArchive.ReadLayerContents(destinationArchive));
        Assert.Equal(config, TestContainerImageArchive.ReadEntryText(destinationArchive, "config.json"));
        Assert.Equal(opaqueBlob, TestContainerImageArchive.ReadEntryText(destinationArchive, "blobs/sha256/attestation"));
        Assert.Equal(sourceEntryNames, TestContainerImageArchive.ReadEntryNames(destinationArchive));

        await Verify(ReadJsonMetadata(destinationArchive, "manifest.json", "repositories"), extension: "json");
    }

    [Fact]
    public async Task RewriteOciContainerdAnnotationsPreservesTagOnlyRepresentation()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceArchive = Path.Combine(workspace.Path, "source.tar");
        var destinationArchive = Path.Combine(workspace.Path, "destination.tar");
        const string ImageName = "widget";
        const string DestinationTag = "latest";
        var index = $$"""
            {
              "schemaVersion": 2,
              "mediaType": "application/vnd.oci.image.index.v1+json",
              "manifests": [
                {
                  "mediaType": "application/vnd.oci.image.manifest.v1+json",
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size": 1234,
                  "annotations": {
                    "org.opencontainers.image.ref.name": "{{SourceTag}}",
                    "io.containerd.image.name": "docker.io/library/widget:{{SourceTag}}",
                    "example.com/unknown": "preserved"
                  },
                  "platform": {
                    "architecture": "amd64",
                    "os": "linux",
                    "os.version": "fixture"
                  },
                  "x-descriptor-field": [ 1, 2, 3 ]
                },
                {
                  "mediaType": "application/vnd.oci.image.manifest.v1+json",
                  "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "size": 4321,
                  "annotations": {
                    "org.opencontainers.image.ref.name": "{{SourceTag}}",
                    "io.containerd.image.name": "docker.io/library/other:{{SourceTag}}",
                    "vnd.docker.reference.type": "attestation-manifest"
                  }
                }
              ],
              "x-index-field": {
                "preserved": true
              }
            }
            """;
        var opaqueManifest = $$"""
            {
              "annotations": {
                "org.opencontainers.image.ref.name": "widget:{{SourceTag}}"
              }
            }
            """;
        TestContainerImageArchive.WriteArchive(
            sourceArchive,
            "oci-layer-payload",
            ("index.json", index),
            ("blobs/sha256/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", opaqueManifest));

        await ContainerImageArchiveRewriter.RewriteImageTagAsync(
            sourceArchive,
            destinationArchive,
            ImageName,
            SourceTag,
            DestinationTag,
            TestContext.Current.CancellationToken);

        Assert.Equal("oci-layer-payload", TestContainerImageArchive.ReadLayerContents(destinationArchive));
        Assert.Equal(
            opaqueManifest,
            TestContainerImageArchive.ReadEntryText(
                destinationArchive,
                "blobs/sha256/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        await Verify(ReadJsonMetadata(destinationArchive, "index.json"), extension: "json");
    }

    [Fact]
    public async Task RewriteOciPodmanAnnotationsPreservesLocalhostQualification()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceArchive = Path.Combine(workspace.Path, "source.tar");
        var destinationArchive = Path.Combine(workspace.Path, "destination.tar");
        const string DestinationTag = "preview-7";
        var index = $$"""
            {
              "schemaVersion": 2,
              "manifests": [
                {
                  "mediaType": "application/vnd.oci.image.manifest.v1+json",
                  "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                  "size": 987,
                  "annotations": {
                    "org.opencontainers.image.ref.name": "localhost/widget:{{SourceTag}}",
                    "example.com/podman-field": "preserved"
                  }
                }
              ]
            }
            """;
        TestContainerImageArchive.WriteArchive(sourceArchive, "podman-layer", ("index.json", index));

        await ContainerImageArchiveRewriter.RewriteImageTagAsync(
            sourceArchive,
            destinationArchive,
            "widget",
            SourceTag,
            DestinationTag,
            TestContext.Current.CancellationToken);

        await Verify(ReadJsonMetadata(destinationArchive, "index.json"), extension: "json");
    }

    [Fact]
    public async Task RewriteHybridArchiveUpdatesEveryNamingFormat()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceArchive = Path.Combine(workspace.Path, "source.tar");
        var destinationArchive = Path.Combine(workspace.Path, "destination.tar");
        const string DestinationTag = "configured";
        var manifest = $$"""
            [
              {
                "Config": "config.json",
                "RepoTags": [ "docker.io/library/widget:{{SourceTag}}" ],
                "Layers": [ "layer.tar" ]
              }
            ]
            """;
        var repositories = $$"""
            {
              "widget": {
                "{{SourceTag}}": "layer-id"
              }
            }
            """;
        var index = $$"""
            {
              "schemaVersion": 2,
              "manifests": [
                {
                  "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
                  "digest": "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                  "size": 456,
                  "annotations": {
                    "org.opencontainers.image.ref.name": "{{SourceTag}}",
                    "io.containerd.image.name": "docker.io/library/widget:{{SourceTag}}"
                  },
                  "platform": {
                    "architecture": "arm64",
                    "os": "linux"
                  }
                }
              ]
            }
            """;
        TestContainerImageArchive.WriteArchive(
            sourceArchive,
            "hybrid-layer",
            ("manifest.json", manifest),
            ("repositories", repositories),
            ("index.json", index),
            ("config.json", """{"architecture":"arm64","os":"linux"}"""));

        await ContainerImageArchiveRewriter.RewriteImageTagAsync(
            sourceArchive,
            destinationArchive,
            "widget",
            SourceTag,
            DestinationTag,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["docker.io/library/widget:configured"],
            TestContainerImageArchive.ReadDockerImageReferences(destinationArchive));
        await Verify(
            ReadJsonMetadata(destinationArchive, "manifest.json", "repositories", "index.json"),
            extension: "json");
    }

    [Theory]
    [InlineData("manifest.json", "{}")]
    [InlineData("repositories", """{"widget":[]}""")]
    [InlineData("index.json", """{"schemaVersion":2}""")]
    [InlineData(
        "index.json",
        """{"schemaVersion":2,"manifests":[{"annotations":{"org.opencontainers.image.ref.name":"aspire-layered-0123456789abcdef"}}]}""")]
    public async Task RewriteRejectsMalformedNamingMetadata(string entryName, string contents)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceArchive = Path.Combine(workspace.Path, "source.tar");
        var destinationArchive = Path.Combine(workspace.Path, "destination.tar");
        TestContainerImageArchive.WriteArchive(sourceArchive, "layer", (entryName, contents));

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            ContainerImageArchiveRewriter.RewriteImageTagAsync(
                sourceArchive,
                destinationArchive,
                "widget",
                SourceTag,
                "latest",
                TestContext.Current.CancellationToken));

        Assert.Contains(entryName, exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destinationArchive));
    }

    [Fact]
    public async Task RewriteRejectsArchiveWithoutMatchingPrivateIdentity()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceArchive = Path.Combine(workspace.Path, "source.tar");
        var destinationArchive = Path.Combine(workspace.Path, "destination.tar");
        TestContainerImageArchive.WriteDockerArchive(sourceArchive, $"other:{SourceTag}", "layer");

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            ContainerImageArchiveRewriter.RewriteImageTagAsync(
                sourceArchive,
                destinationArchive,
                "widget",
                SourceTag,
                "latest",
                TestContext.Current.CancellationToken));

        Assert.Contains($"widget:{SourceTag}", exception.Message, StringComparison.Ordinal);
        Assert.Equal([$"other:{SourceTag}"], TestContainerImageArchive.ReadDockerImageReferences(sourceArchive));
        Assert.False(File.Exists(destinationArchive));
    }

    [Fact]
    public async Task RewriteRejectsArchiveWithoutNamingMetadata()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceArchive = Path.Combine(workspace.Path, "source.tar");
        var destinationArchive = Path.Combine(workspace.Path, "destination.tar");
        TestContainerImageArchive.WriteArchive(
            sourceArchive,
            "layer",
            ("config.json", """{"architecture":"amd64","os":"linux"}"""));

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            ContainerImageArchiveRewriter.RewriteImageTagAsync(
                sourceArchive,
                destinationArchive,
                "widget",
                SourceTag,
                "latest",
                TestContext.Current.CancellationToken));

        Assert.Contains("does not contain supported naming metadata", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destinationArchive));
    }

    [Fact]
    public async Task RewriteRejectsExistingDestinationIdentity()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceArchive = Path.Combine(workspace.Path, "source.tar");
        var destinationArchive = Path.Combine(workspace.Path, "destination.tar");
        var manifest = $$"""
            [
              {
                "Config": "config.json",
                "RepoTags": [
                  "widget:{{SourceTag}}",
                  "widget:latest"
                ],
                "Layers": [ "layer.tar" ]
              }
            ]
            """;
        TestContainerImageArchive.WriteArchive(sourceArchive, "layer", ("manifest.json", manifest));

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            ContainerImageArchiveRewriter.RewriteImageTagAsync(
                sourceArchive,
                destinationArchive,
                "widget",
                SourceTag,
                "latest",
                TestContext.Current.CancellationToken));

        Assert.Contains("both source identity", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destinationArchive));
    }

    [Fact]
    public async Task RewriteHonorsCancellationWithoutLeavingDestination()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceArchive = Path.Combine(workspace.Path, "source.tar");
        var destinationArchive = Path.Combine(workspace.Path, "destination.tar");
        TestContainerImageArchive.WriteDockerArchive(sourceArchive, $"widget:{SourceTag}", "layer");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ContainerImageArchiveRewriter.RewriteImageTagAsync(
                sourceArchive,
                destinationArchive,
                "widget",
                SourceTag,
                "latest",
                cancellation.Token));

        Assert.False(File.Exists(destinationArchive));
    }

    [Fact]
    public async Task RewriteDoesNotOverwriteExistingDestination()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceArchive = Path.Combine(workspace.Path, "source.tar");
        var destinationArchive = Path.Combine(workspace.Path, "destination.tar");
        TestContainerImageArchive.WriteDockerArchive(sourceArchive, $"widget:{SourceTag}", "layer");
        await File.WriteAllTextAsync(destinationArchive, "existing artifact", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() =>
            ContainerImageArchiveRewriter.RewriteImageTagAsync(
                sourceArchive,
                destinationArchive,
                "widget",
                SourceTag,
                "latest",
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "existing artifact",
            await File.ReadAllTextAsync(destinationArchive, TestContext.Current.CancellationToken));
        Assert.Equal([$"widget:{SourceTag}"], TestContainerImageArchive.ReadDockerImageReferences(sourceArchive));
    }

    [Fact]
    public void FixtureReadersRecognizeGzipByMagicBytes()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sourceArchive = Path.Combine(workspace.Path, "source.tar");
        var compressedArchive = Path.Combine(workspace.Path, "custom-extension.archive");
        TestContainerImageArchive.WriteDockerArchive(sourceArchive, "widget:latest", "compressed-layer");

        using (var source = File.OpenRead(sourceArchive))
        using (var destination = File.Create(compressedArchive))
        using (var gzip = new GZipStream(destination, CompressionLevel.SmallestSize))
        {
            source.CopyTo(gzip);
        }

        Assert.Equal(["widget:latest"], TestContainerImageArchive.ReadDockerImageReferences(compressedArchive));
        Assert.Equal("compressed-layer", TestContainerImageArchive.ReadLayerContents(compressedArchive));
    }

    private static string ReadJsonMetadata(string archivePath, params string[] entryNames)
    {
        var metadata = new JsonObject();
        foreach (var entryName in entryNames)
        {
            metadata[entryName] = JsonNode.Parse(TestContainerImageArchive.ReadEntryText(archivePath, entryName));
        }

        return metadata.ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        });
    }
}
