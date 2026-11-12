// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Rewrites container image names stored in archive-level metadata.
/// </summary>
internal static class ContainerImageArchiveRewriter
{
    private const int MaxNamingMetadataLength = 16 * 1024 * 1024;
    private const string OciReferenceNameAnnotation = "org.opencontainers.image.ref.name";
    private const string ContainerdImageNameAnnotation = "io.containerd.image.name";

    /// <summary>
    /// Replaces an invocation-private image tag with the configured destination tag.
    /// </summary>
    /// <param name="sourceArchivePath">The staged uncompressed container image archive.</param>
    /// <param name="destinationArchivePath">The new path to write the rewritten archive to.</param>
    /// <param name="imageName">The image repository, without a tag.</param>
    /// <param name="sourceTag">The invocation-private tag currently stored in the archive.</param>
    /// <param name="destinationTag">The configured tag to store in the rewritten archive.</param>
    /// <param name="cancellationToken">A token that cancels the rewrite.</param>
    /// <returns>A task that represents the archive rewrite.</returns>
    internal static async Task RewriteImageTagAsync(
        string sourceArchivePath,
        string destinationArchivePath,
        string imageName,
        string sourceTag,
        string destinationTag,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationTag);

        if (!IsValidTag(sourceTag))
        {
            throw new ArgumentException($"'{sourceTag}' is not a valid container image tag.", nameof(sourceTag));
        }

        if (!IsValidTag(destinationTag))
        {
            throw new ArgumentException($"'{destinationTag}' is not a valid container image tag.", nameof(destinationTag));
        }

        if (string.Equals(sourceTag, destinationTag, StringComparison.Ordinal))
        {
            throw new ArgumentException("The source and destination image tags must be different.", nameof(destinationTag));
        }

        ValidateRepositoryName(imageName);

        var sourceFullPath = Path.GetFullPath(sourceArchivePath);
        var destinationFullPath = Path.GetFullPath(destinationArchivePath);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(sourceFullPath, destinationFullPath, pathComparison))
        {
            throw new ArgumentException("The source and destination archive paths must be different.", nameof(destinationArchivePath));
        }

        var rewriteContext = new RewriteContext(imageName, sourceTag, destinationTag);
        var destinationCreated = false;
        var completed = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = new FileStream(
                sourceFullPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            await using (source.ConfigureAwait(false))
            {
                var destination = new FileStream(
                    destinationFullPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        Options = FileOptions.Asynchronous
                    });
                await using (destination.ConfigureAwait(false))
                {
                    destinationCreated = true;

                    await RewriteArchiveAsync(source, destination, rewriteContext, cancellationToken).ConfigureAwait(false);

                    if (rewriteContext.SourceReferenceCount == 0)
                    {
                        throw new DistributedApplicationException(
                            $"Container image archive '{sourceArchivePath}' does not contain supported naming metadata for '{imageName}:{sourceTag}'.");
                    }

                    if (rewriteContext.DestinationReferenceCount > 0)
                    {
                        throw new DistributedApplicationException(
                            $"Container image archive '{sourceArchivePath}' contains both source identity " +
                            $"'{imageName}:{sourceTag}' and destination identity '{imageName}:{destinationTag}'.");
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            completed = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DistributedApplicationException)
        {
            throw;
        }
        catch (EndOfStreamException ex)
        {
            throw new DistributedApplicationException(
                $"Container image archive '{sourceArchivePath}' ended before a complete tar entry could be read.",
                ex);
        }
        catch (InvalidDataException ex)
        {
            throw new DistributedApplicationException(
                $"Container image archive '{sourceArchivePath}' is not a valid tar archive: {ex.Message}",
                ex);
        }
        finally
        {
            if (destinationCreated && !completed)
            {
                TryDeleteIncompleteDestination(destinationFullPath);
            }
        }
    }

    private static async Task RewriteArchiveAsync(
        Stream source,
        Stream destination,
        RewriteContext rewriteContext,
        CancellationToken cancellationToken)
    {
        var sawEntry = false;
        var sawDockerManifest = false;
        var sawLegacyRepositories = false;
        var sawOciIndex = false;
        var reader = new TarReader(source, leaveOpen: true);
        await using (reader.ConfigureAwait(false))
        {
            using var writer = new TarWriter(destination, leaveOpen: true);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                sawEntry = true;
                var rootEntryName = GetRootEntryName(entry.Name);
                byte[]? rewrittenContents = null;

                switch (rootEntryName)
                {
                    case "manifest.json":
                        EnsureUniqueNamingEntry(ref sawDockerManifest, entry.Name);
                        rewrittenContents = RewriteDockerManifest(
                            await ReadNamingMetadataAsync(entry, cancellationToken).ConfigureAwait(false),
                            rewriteContext,
                            entry.Name);
                        break;
                    case "repositories":
                        EnsureUniqueNamingEntry(ref sawLegacyRepositories, entry.Name);
                        rewrittenContents = RewriteLegacyRepositories(
                            await ReadNamingMetadataAsync(entry, cancellationToken).ConfigureAwait(false),
                            rewriteContext,
                            entry.Name);
                        break;
                    case "index.json":
                        EnsureUniqueNamingEntry(ref sawOciIndex, entry.Name);
                        rewrittenContents = RewriteOciIndex(
                            await ReadNamingMetadataAsync(entry, cancellationToken).ConfigureAwait(false),
                            rewriteContext,
                            entry.Name);
                        break;
                }

                // Reusing the reader-created TarEntry preserves its tar format and metadata. Each entry
                // is written before advancing the reader so layer streams never need to be buffered.
                if (rewrittenContents is null)
                {
                    await writer.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    using var rewrittenStream = new MemoryStream(rewrittenContents, writable: false);
                    entry.DataStream = rewrittenStream;
                    await writer.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (!sawEntry)
        {
            throw new DistributedApplicationException("The container image archive is empty.");
        }
    }

    private static byte[] RewriteDockerManifest(byte[] contents, RewriteContext rewriteContext, string entryName)
    {
        // Docker save records archive aliases as:
        //   [{"Config":"config.json","RepoTags":["registry/repo:tag"],"Layers":["layer.tar"]}]
        // Keep config and layer references opaque because their contents remain content-addressed.
        // https://github.com/moby/moby/blob/v28.0.0/image/tarexport/save.go
        if (ParseJson(contents, entryName) is not JsonArray manifests)
        {
            throw InvalidMetadata(entryName, "the root value must be an array");
        }

        foreach (var manifestNode in manifests)
        {
            if (manifestNode is not JsonObject manifest)
            {
                throw InvalidMetadata(entryName, "each manifest must be an object");
            }

            if (!manifest.TryGetPropertyValue("RepoTags", out var repoTagsNode) || repoTagsNode is null)
            {
                continue;
            }

            if (repoTagsNode is not JsonArray repoTags)
            {
                throw InvalidMetadata(entryName, "'RepoTags' must be an array when present");
            }

            for (var index = 0; index < repoTags.Count; index++)
            {
                var reference = GetRequiredString(repoTags[index], entryName, $"'RepoTags[{index}]'");
                if (!rewriteContext.TryClassifyTaggedReference(reference, out var match, out var rewrittenReference))
                {
                    throw InvalidMetadata(entryName, $"'{reference}' is not a tagged image reference");
                }

                switch (match)
                {
                    case ReferenceMatch.Source:
                        rewriteContext.RecordSourceReference();
                        repoTags[index] = rewrittenReference;
                        break;
                    case ReferenceMatch.Destination:
                        rewriteContext.RecordDestinationReference();
                        break;
                }
            }
        }

        return SerializeJson(manifests);
    }

    private static byte[] RewriteLegacyRepositories(byte[] contents, RewriteContext rewriteContext, string entryName)
    {
        // The legacy Docker mapping is keyed first by repository and then by tag:
        //   {"registry/repo":{"tag":"layer-id"}}
        // Rename only the tag key so the existing layer identifier remains untouched.
        // https://github.com/moby/moby/blob/v28.0.0/image/tarexport/save.go
        if (ParseJson(contents, entryName) is not JsonObject repositories)
        {
            throw InvalidMetadata(entryName, "the root value must be an object");
        }

        var rewrittenRepositories = new JsonObject();
        foreach (var repository in repositories)
        {
            if (repository.Value is not JsonObject tags)
            {
                throw InvalidMetadata(entryName, $"repository '{repository.Key}' must map tags to layer identifiers");
            }

            var matchingRepository = rewriteContext.IsMatchingRepository(repository.Key);
            if (matchingRepository &&
                tags.ContainsKey(rewriteContext.SourceTag) &&
                tags.ContainsKey(rewriteContext.DestinationTag))
            {
                throw InvalidMetadata(
                    entryName,
                    $"repository '{repository.Key}' contains both source tag '{rewriteContext.SourceTag}' " +
                    $"and destination tag '{rewriteContext.DestinationTag}'");
            }

            var rewrittenTags = new JsonObject();
            foreach (var tag in tags)
            {
                _ = GetRequiredString(
                    tag.Value,
                    entryName,
                    $"repository '{repository.Key}' tag '{tag.Key}'");

                var tagName = tag.Key;
                if (matchingRepository && string.Equals(tag.Key, rewriteContext.SourceTag, StringComparison.Ordinal))
                {
                    rewriteContext.RecordSourceReference();
                    tagName = rewriteContext.DestinationTag;
                }
                else if (matchingRepository && string.Equals(tag.Key, rewriteContext.DestinationTag, StringComparison.Ordinal))
                {
                    rewriteContext.RecordDestinationReference();
                }

                rewrittenTags.Add(tagName, tag.Value?.DeepClone());
            }

            rewrittenRepositories.Add(repository.Key, rewrittenTags);
        }

        return SerializeJson(rewrittenRepositories);
    }

    private static byte[] RewriteOciIndex(byte[] contents, RewriteContext rewriteContext, string entryName)
    {
        // Containerd commonly emits a tag-only ref.name paired with a canonical full image.name,
        // while Podman can put the full reference directly in ref.name:
        //   "org.opencontainers.image.ref.name": "tag"
        //   "io.containerd.image.name": "docker.io/library/repo:tag"
        // Root descriptors are the OCI naming boundary; referenced manifests and blobs are opaque.
        // https://github.com/containerd/containerd/blob/v2.0.0/core/images/archive/exporter.go
        // https://github.com/opencontainers/image-spec/blob/v1.1.1/image-layout.md
        if (ParseJson(contents, entryName) is not JsonObject index)
        {
            throw InvalidMetadata(entryName, "the root value must be an object");
        }

        if (index["manifests"] is not JsonArray manifests)
        {
            throw InvalidMetadata(entryName, "'manifests' must be an array");
        }

        for (var descriptorIndex = 0; descriptorIndex < manifests.Count; descriptorIndex++)
        {
            if (manifests[descriptorIndex] is not JsonObject descriptor)
            {
                throw InvalidMetadata(entryName, $"descriptor {descriptorIndex} must be an object");
            }

            if (!descriptor.TryGetPropertyValue("annotations", out var annotationsNode) || annotationsNode is null)
            {
                continue;
            }

            if (annotationsNode is not JsonObject annotations)
            {
                throw InvalidMetadata(entryName, $"descriptor {descriptorIndex} 'annotations' must be an object");
            }

            var referenceName = GetOptionalAnnotationString(annotations, OciReferenceNameAnnotation, entryName, descriptorIndex);
            var containerdImageName = GetOptionalAnnotationString(annotations, ContainerdImageNameAnnotation, entryName, descriptorIndex);

            var referenceNameIsFull = false;
            var referenceNameMatch = ReferenceMatch.Unrelated;
            string? rewrittenReferenceName = null;
            if (referenceName is not null)
            {
                if (rewriteContext.TryClassifyTaggedReference(referenceName, out referenceNameMatch, out rewrittenReferenceName))
                {
                    referenceNameIsFull = true;
                }
                else if (IsValidTag(referenceName))
                {
                    referenceNameMatch = rewriteContext.ClassifyTag(referenceName);
                    rewrittenReferenceName = referenceNameMatch == ReferenceMatch.Source
                        ? rewriteContext.DestinationTag
                        : referenceName;
                }
                else
                {
                    throw InvalidMetadata(
                        entryName,
                        $"descriptor {descriptorIndex} annotation '{OciReferenceNameAnnotation}' is not a tag or tagged image reference");
                }
            }

            var containerdImageNameMatch = ReferenceMatch.Unrelated;
            string? rewrittenContainerdImageName = null;
            if (containerdImageName is not null &&
                !rewriteContext.TryClassifyTaggedReference(containerdImageName, out containerdImageNameMatch, out rewrittenContainerdImageName))
            {
                throw InvalidMetadata(
                    entryName,
                    $"descriptor {descriptorIndex} annotation '{ContainerdImageNameAnnotation}' is not a tagged image reference");
            }

            if (!referenceNameIsFull &&
                referenceNameMatch == ReferenceMatch.Source &&
                containerdImageName is null)
            {
                throw InvalidMetadata(
                    entryName,
                    $"descriptor {descriptorIndex} has a tag-only '{OciReferenceNameAnnotation}' without a full '{ContainerdImageNameAnnotation}'");
            }

            if (!referenceNameIsFull &&
                referenceNameMatch == ReferenceMatch.Source &&
                containerdImageName is not null &&
                rewriteContext.IsTaggedReferenceForImage(containerdImageName) &&
                containerdImageNameMatch != ReferenceMatch.Source)
            {
                throw InvalidMetadata(
                    entryName,
                    $"descriptor {descriptorIndex} has inconsistent '{OciReferenceNameAnnotation}' and '{ContainerdImageNameAnnotation}' annotations");
            }

            var sourceAttribution =
                (referenceNameIsFull && referenceNameMatch == ReferenceMatch.Source) ||
                containerdImageNameMatch == ReferenceMatch.Source;
            var destinationAttribution =
                (referenceNameIsFull && referenceNameMatch == ReferenceMatch.Destination) ||
                containerdImageNameMatch == ReferenceMatch.Destination;

            if (sourceAttribution && destinationAttribution)
            {
                throw InvalidMetadata(entryName, $"descriptor {descriptorIndex} mixes the source and destination image identities");
            }

            if (sourceAttribution)
            {
                EnsureOciAnnotationsAgree(
                    entryName,
                    descriptorIndex,
                    referenceName,
                    referenceNameMatch,
                    containerdImageName,
                    containerdImageNameMatch,
                    ReferenceMatch.Source);

                if (referenceName is not null)
                {
                    rewriteContext.RecordSourceReference();
                    annotations[OciReferenceNameAnnotation] = rewrittenReferenceName;
                }

                if (containerdImageName is not null)
                {
                    rewriteContext.RecordSourceReference();
                    annotations[ContainerdImageNameAnnotation] = rewrittenContainerdImageName;
                }
            }
            else if (destinationAttribution)
            {
                EnsureOciAnnotationsAgree(
                    entryName,
                    descriptorIndex,
                    referenceName,
                    referenceNameMatch,
                    containerdImageName,
                    containerdImageNameMatch,
                    ReferenceMatch.Destination);

                if (referenceName is not null)
                {
                    rewriteContext.RecordDestinationReference();
                }

                if (containerdImageName is not null)
                {
                    rewriteContext.RecordDestinationReference();
                }
            }
        }

        return SerializeJson(index);
    }

    private static void EnsureOciAnnotationsAgree(
        string entryName,
        int descriptorIndex,
        string? referenceName,
        ReferenceMatch referenceNameMatch,
        string? containerdImageName,
        ReferenceMatch containerdImageNameMatch,
        ReferenceMatch expectedMatch)
    {
        if ((referenceName is not null && referenceNameMatch != expectedMatch) ||
            (containerdImageName is not null && containerdImageNameMatch != expectedMatch))
        {
            throw InvalidMetadata(
                entryName,
                $"descriptor {descriptorIndex} has inconsistent '{OciReferenceNameAnnotation}' and '{ContainerdImageNameAnnotation}' annotations");
        }
    }

    private static async Task<byte[]> ReadNamingMetadataAsync(TarEntry entry, CancellationToken cancellationToken)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
        {
            throw InvalidMetadata(entry.Name, "the entry must be a regular file");
        }

        if (entry.Length > MaxNamingMetadataLength)
        {
            throw InvalidMetadata(entry.Name, $"the entry exceeds the {MaxNamingMetadataLength}-byte safety limit");
        }

        var dataStream = entry.DataStream ?? throw InvalidMetadata(entry.Name, "the entry has no data");
        using var buffer = new MemoryStream(entry.Length > 0 ? checked((int)entry.Length) : 0);
        await dataStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static JsonNode ParseJson(byte[] contents, string entryName)
    {
        try
        {
            return JsonNode.Parse(contents)
                ?? throw InvalidMetadata(entryName, "the JSON document is empty");
        }
        catch (JsonException ex)
        {
            throw new DistributedApplicationException(
                $"Container image archive entry '{entryName}' contains invalid JSON: {ex.Message}",
                ex);
        }
        catch (ArgumentException ex)
        {
            throw new DistributedApplicationException(
                $"Container image archive entry '{entryName}' contains ambiguous JSON properties: {ex.Message}",
                ex);
        }
    }

    private static byte[] SerializeJson(JsonNode node)
    {
        return JsonSerializer.SerializeToUtf8Bytes(node);
    }

    private static string GetRequiredString(JsonNode? node, string entryName, string location)
    {
        if (node is JsonValue value &&
            value.TryGetValue<string>(out var result) &&
            !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw InvalidMetadata(entryName, $"{location} must be a non-empty string");
    }

    private static string? GetOptionalAnnotationString(
        JsonObject annotations,
        string annotationName,
        string entryName,
        int descriptorIndex)
    {
        if (!annotations.TryGetPropertyValue(annotationName, out var value))
        {
            return null;
        }

        return GetRequiredString(
            value,
            entryName,
            $"descriptor {descriptorIndex} annotation '{annotationName}'");
    }

    private static string? GetRootEntryName(string entryName)
    {
        while (entryName.StartsWith("./", StringComparison.Ordinal))
        {
            entryName = entryName[2..];
        }

        return entryName.Contains('/') ? null : entryName;
    }

    private static void EnsureUniqueNamingEntry(ref bool seen, string entryName)
    {
        if (seen)
        {
            throw new DistributedApplicationException(
                $"Container image archive contains more than one root '{GetRootEntryName(entryName)}' entry.");
        }

        seen = true;
    }

    private static DistributedApplicationException InvalidMetadata(string entryName, string reason)
    {
        return new DistributedApplicationException(
            $"Container image archive entry '{entryName}' has invalid naming metadata: {reason}.");
    }

    private static bool IsValidTag(string tag)
    {
        if (tag.Length is 0 or > 128 || !IsTagStartCharacter(tag[0]))
        {
            return false;
        }

        foreach (var character in tag.AsSpan(1))
        {
            if (!IsTagCharacter(character))
            {
                return false;
            }
        }

        return true;

        static bool IsTagStartCharacter(char character)
            => char.IsAsciiLetterOrDigit(character) || character == '_';

        static bool IsTagCharacter(char character)
            => IsTagStartCharacter(character) || character is '.' or '-';
    }

    private static void ValidateRepositoryName(string imageName)
    {
        if (!string.Equals(imageName, imageName.Trim(), StringComparison.Ordinal) ||
            imageName.Contains('\\') ||
            imageName.Contains('@') ||
            imageName.StartsWith("/", StringComparison.Ordinal) ||
            imageName.EndsWith("/", StringComparison.Ordinal) ||
            imageName.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{imageName}' is not a valid container image repository.", nameof(imageName));
        }

        var lastSlash = imageName.LastIndexOf('/');
        var lastColon = imageName.LastIndexOf(':');
        if (lastColon > lastSlash)
        {
            throw new ArgumentException("The image repository must not include a tag.", nameof(imageName));
        }
    }

    private static void TryDeleteIncompleteDestination(string destinationPath)
    {
        try
        {
            File.Delete(destinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preserve the primary rewrite failure. The destination was created with CreateNew, so
            // a cleanup failure cannot overwrite a pre-existing consumer artifact.
        }
    }

    private enum ReferenceMatch
    {
        Unrelated,
        Source,
        Destination
    }

    private sealed class RewriteContext
    {
        private readonly HashSet<string> _repositoryAliases;

        public RewriteContext(string imageName, string sourceTag, string destinationTag)
        {
            _repositoryAliases = CreateRepositoryAliases(imageName);
            SourceTag = sourceTag;
            DestinationTag = destinationTag;
        }

        public string SourceTag { get; }
        public string DestinationTag { get; }
        public int SourceReferenceCount { get; private set; }
        public int DestinationReferenceCount { get; private set; }

        public void RecordSourceReference() => SourceReferenceCount++;

        public void RecordDestinationReference() => DestinationReferenceCount++;

        public ReferenceMatch ClassifyTag(string tag)
        {
            if (string.Equals(tag, SourceTag, StringComparison.Ordinal))
            {
                return ReferenceMatch.Source;
            }

            return string.Equals(tag, DestinationTag, StringComparison.Ordinal)
                ? ReferenceMatch.Destination
                : ReferenceMatch.Unrelated;
        }

        public bool IsMatchingRepository(string repository) => _repositoryAliases.Contains(repository);

        public bool IsTaggedReferenceForImage(string reference)
        {
            return TrySplitTaggedReference(reference, out var repository, out _) &&
                IsMatchingRepository(repository);
        }

        public bool TryClassifyTaggedReference(
            string reference,
            out ReferenceMatch match,
            out string? rewrittenReference)
        {
            if (!TrySplitTaggedReference(reference, out var repository, out var tag))
            {
                match = ReferenceMatch.Unrelated;
                rewrittenReference = null;
                return false;
            }

            match = IsMatchingRepository(repository)
                ? ClassifyTag(tag)
                : ReferenceMatch.Unrelated;
            rewrittenReference = match == ReferenceMatch.Source
                ? $"{repository}:{DestinationTag}"
                : reference;
            return true;
        }

        private static HashSet<string> CreateRepositoryAliases(string imageName)
        {
            // Docker can canonicalize a familiar name such as "widget" to
            // "docker.io/library/widget", while Podman can qualify it as "localhost/widget".
            // Keep the supplied repository exact when it already names a non-Docker registry.
            var aliases = new HashSet<string>(StringComparer.Ordinal)
            {
                imageName
            };

            var firstSlash = imageName.IndexOf('/');
            var firstComponent = firstSlash >= 0 ? imageName[..firstSlash] : imageName;
            if (!HasExplicitRegistry(firstComponent))
            {
                aliases.Add($"localhost/{imageName}");
                aliases.Add(firstSlash >= 0
                    ? $"docker.io/{imageName}"
                    : $"docker.io/library/{imageName}");
                return aliases;
            }

            if (firstSlash >= 0 &&
                firstComponent is "docker.io" or "index.docker.io")
            {
                var repositoryPath = imageName[(firstSlash + 1)..];
                if (!repositoryPath.Contains('/'))
                {
                    repositoryPath = $"library/{repositoryPath}";
                }

                aliases.Add($"docker.io/{repositoryPath}");
                aliases.Add($"index.docker.io/{repositoryPath}");
                aliases.Add(repositoryPath.StartsWith("library/", StringComparison.Ordinal) &&
                    !repositoryPath["library/".Length..].Contains('/')
                        ? repositoryPath["library/".Length..]
                        : repositoryPath);
            }

            return aliases;
        }

        private static bool HasExplicitRegistry(string firstComponent)
        {
            return firstComponent.Contains('.') ||
                firstComponent.Contains(':') ||
                string.Equals(firstComponent, "localhost", StringComparison.Ordinal);
        }

        private static bool TrySplitTaggedReference(string reference, out string repository, out string tag)
        {
            repository = string.Empty;
            tag = string.Empty;

            if (string.IsNullOrWhiteSpace(reference) ||
                !string.Equals(reference, reference.Trim(), StringComparison.Ordinal) ||
                reference.Contains('\\') ||
                reference.Contains('@'))
            {
                return false;
            }

            var lastSlash = reference.LastIndexOf('/');
            var lastColon = reference.LastIndexOf(':');
            if (lastColon <= lastSlash || lastColon <= 0 || lastColon == reference.Length - 1)
            {
                return false;
            }

            repository = reference[..lastColon];
            tag = reference[(lastColon + 1)..];
            return !string.IsNullOrWhiteSpace(repository) &&
                !repository.StartsWith("/", StringComparison.Ordinal) &&
                !repository.EndsWith("/", StringComparison.Ordinal) &&
                !repository.Contains("//", StringComparison.Ordinal) &&
                IsValidTag(tag);
        }
    }
}
