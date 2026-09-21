// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents;

/// <summary>
/// Applies grouped JSON mutations with semantic no-ops, stale-input checks and atomic replacement.
/// </summary>
internal sealed class AgentConfigurationWriter(ILogger<AgentConfigurationWriter> logger)
{
    public async Task<IReadOnlyList<AgentTargetResult>> ApplyAsync(
        IEnumerable<AgentConfigurationTarget> targets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<AgentTargetResult>();
        foreach (var file in GroupTargets(targets, results))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new List<AgentTargetResult>();
            try
            {
                // A reader belongs to one commit, so a later file observes earlier successful
                // writes rather than reusing a discovery snapshot or our own stale policy reads.
                var reader = new ReadContext();
                var document = await reader.ReadAsync(file.Path, cancellationToken);
                foreach (var alias in file.Aliases)
                {
                    if (!AgentPath.Comparer.Equals((await reader.ReadAsync(alias, cancellationToken)).Path, file.Path))
                    {
                        throw new AgentConfigurationException(AgentCommandStrings.Configuration_ConcurrentChange);
                    }
                }

                var root = document.Root?.DeepClone().AsObject() ?? new JsonObject();
                foreach (var target in file.Targets)
                {
                    var candidate = root.DeepClone().AsObject();
                    try
                    {
                        var edit = await target.ApplyAsync(candidate, reader, cancellationToken);
                        var status = edit.Status;
                        if (status is AgentConfigurationStatus.Configured)
                        {
                            status = JsonNode.DeepEquals(root, candidate) ? AgentConfigurationStatus.Unchanged : AgentConfigurationStatus.Configured;
                            root = candidate;
                        }

                        pending.Add(target.ToResult(status, edit.Message));
                    }
                    catch (AgentConfigurationException ex)
                    {
                        pending.Add(target.ToResult(AgentConfigurationStatus.Blocked, ex.Message));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogDebug(ex, "Could not prepare agent configuration at {Path}.", target.Path);
                        pending.Add(target.ToResult(AgentConfigurationStatus.Failed,
                            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.Configuration_ReadWriteFailed, ex.Message)));
                    }
                }

                if (!JsonNode.DeepEquals(document.Root ?? new JsonObject(), root))
                {
                    await SaveAsync(document, root, reader, cancellationToken);
                }
                else if (!await reader.IsCurrentAsync(cancellationToken))
                {
                    throw new AgentConfigurationException(AgentCommandStrings.Configuration_ConcurrentChange);
                }
            }
            catch (AgentConfigurationException ex)
            {
                CompleteFailure(file, pending, AgentConfigurationStatus.Blocked, ex.Message, stale: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not write agent configuration at {Path}.", file.Path);
                CompleteFailure(file, pending, AgentConfigurationStatus.Failed,
                    string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.Configuration_ReadWriteFailed, ex.Message), stale: false);
            }

            results.AddRange(pending);
        }

        return results;
    }

    private static IReadOnlyList<ConfigurationFile> GroupTargets(
        IEnumerable<AgentConfigurationTarget> targets,
        List<AgentTargetResult> results)
    {
        var files = new Dictionary<string, List<AgentConfigurationTarget>>(AgentPath.Comparer);
        foreach (var target in targets)
        {
            try
            {
                var path = AgentPath.Resolve(target.Path);
                if (!files.TryGetValue(path, out var entries))
                {
                    entries = [];
                    files.Add(path, entries);
                }

                entries.Add(target with { Path = Path.GetFullPath(target.Path) });
            }
            catch (AgentConfigurationException ex)
            {
                results.Add(target.ToResult(AgentConfigurationStatus.Blocked, ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                results.Add(target.ToResult(AgentConfigurationStatus.Failed,
                    string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.Configuration_ReadWriteFailed, ex.Message)));
            }
        }

        return files.Select(file => new ConfigurationFile(
            file.Key,
            file.Value.Select(target => target.Path).Distinct(AgentPath.Comparer).ToArray(),
            file.Value.GroupBy(target => (target.Asset, target.Entry))
                .Select(group => group.First() with
                {
                    Scope = group.Any(target => target.Scope is AgentConfigurationScope.User) ? AgentConfigurationScope.User : AgentConfigurationScope.Project,
                    Clients = group.SelectMany(target => target.Clients).Distinct().ToArray(),
                    ApplyAsync = async (root, context, cancellationToken) =>
                    {
                        // Sharing a physical entry must not bypass either client's policy.
                        // All contributions validate before the merged file is saved once.
                        AgentConfigurationEdit? edit = null;
                        AgentConfigurationEdit? skipped = null;
                        foreach (var target in group)
                        {
                            edit = await target.ApplyAsync(root, context, cancellationToken);
                            if (edit.Status is AgentConfigurationStatus.Blocked or AgentConfigurationStatus.Failed)
                            {
                                return edit;
                            }

                            if (edit.Status is AgentConfigurationStatus.Skipped)
                            {
                                skipped = edit;
                            }
                        }

                        return skipped ?? edit!;
                    }
                })
                .OrderBy(target => target.Asset is AgentAssetKind.TelemetryHooks)
                .ToArray()))
            // Keep advisory hook writes after native configuration. Claude's user plugin
            // settings and hook share a file and are committed together.
            .OrderBy(file => file.Targets.Any(target => target.Asset is AgentAssetKind.TelemetryHooks))
            .ThenBy(file => file.Targets.All(target => target.Asset is AgentAssetKind.TelemetryHooks))
            .ToArray();
    }

    private static async Task SaveAsync(
        ReadContext.Document document,
        JsonObject root,
        ReadContext reader,
        CancellationToken cancellationToken)
    {
        await AgentFileCommitter.CommitAsync(
            document.Path,
            destinationExists: document.Bytes is not null,
            (stream, token) => JsonSerializer.SerializeAsync(stream, root, JsonSourceGenerationContext.Default.JsonObject, token),
            async token =>
            {
                if (!await reader.IsCurrentAsync(token))
                {
                    throw new AgentConfigurationException(AgentCommandStrings.Configuration_ConcurrentChange);
                }
            },
            newFileMode: UnixFileMode.UserRead | UnixFileMode.UserWrite,
            cancellationToken);
    }

    private static void CompleteFailure(
        ConfigurationFile file,
        List<AgentTargetResult> pending,
        AgentConfigurationStatus status,
        string message,
        bool stale)
    {
        for (var index = 0; index < pending.Count; index++)
        {
            if (pending[index].Status is AgentConfigurationStatus.Configured ||
                (stale && pending[index].Status is AgentConfigurationStatus.Unchanged))
            {
                pending[index] = pending[index] with { Status = status, Message = message };
            }
        }

        foreach (var target in file.Targets.Skip(pending.Count))
        {
            pending.Add(target.ToResult(status, message));
        }
    }

    /// <summary>
    /// Tracks a write's original bytes and policy inputs for optimistic concurrency checks.
    /// </summary>
    internal sealed class ReadContext
    {
        private readonly Dictionary<string, Document> _documents = new(AgentPath.Comparer);
        private readonly Dictionary<string, string> _aliases = new(AgentPath.Comparer);

        public async Task<Document> ReadAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string physicalPath;
            try
            {
                physicalPath = AgentPath.Resolve(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                throw new AgentConfigurationException(AgentCommandStrings.Configuration_UnsupportedOverride);
            }

            _aliases[path] = physicalPath;
            if (_documents.TryGetValue(physicalPath, out var existing))
            {
                return existing;
            }

            var bytes = await ReadBytesAsync(physicalPath, cancellationToken);
            var root = bytes is null ? null : AgentConfigurationJson.ParseObject(bytes);
            var document = new Document(physicalPath, bytes, root);
            _documents.Add(physicalPath, document);
            return document;
        }

        public async Task<JsonObject?> ReadOptionalAsync(string path, CancellationToken cancellationToken)
            => (await ReadAsync(path, cancellationToken)).Root?.DeepClone().AsObject();

        public async Task<bool> IsCurrentAsync(CancellationToken cancellationToken)
        {
            foreach (var (alias, physicalPath) in _aliases)
            {
                if (!AgentPath.Comparer.Equals(AgentPath.Resolve(alias), physicalPath))
                {
                    return false;
                }
            }

            foreach (var document in _documents.Values)
            {
                var current = await ReadBytesAsync(document.Path, cancellationToken);
                if (document.Bytes is null ? current is not null : current is null || !document.Bytes.AsSpan().SequenceEqual(current))
                {
                    return false;
                }
            }

            return true;
        }

        private static async Task<byte[]?> ReadBytesAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                return await File.ReadAllBytesAsync(path, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        internal sealed record Document(string Path, byte[]? Bytes, JsonObject? Root);
    }

    private sealed record ConfigurationFile(string Path, IReadOnlyList<string> Aliases, IReadOnlyList<AgentConfigurationTarget> Targets);
}
