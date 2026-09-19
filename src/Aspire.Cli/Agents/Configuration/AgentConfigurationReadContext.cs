// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.Configuration;

/// <summary>
/// Retains original bytes for optimistic concurrency checks, including read-only policy inputs.
/// </summary>
internal sealed class AgentConfigurationReadContext
{
    private readonly Dictionary<string, AgentConfigurationDocument> _documents = new(AgentConfigurationPath.Comparer);
    private readonly Dictionary<string, string> _aliases = new(AgentConfigurationPath.Comparer);

    public async Task<AgentConfigurationDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string physicalPath;
        try
        {
            physicalPath = AgentConfigurationPath.Resolve(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new AgentConfigurationException(AgentConfigurationStrings.UnsupportedOverride);
        }

        _aliases[path] = physicalPath;
        if (_documents.TryGetValue(physicalPath, out var existing))
        {
            return existing;
        }

        var bytes = await ReadBytesAsync(physicalPath, cancellationToken);
        var root = bytes is null ? null : AgentConfigurationJson.ParseObject(bytes);

        var document = new AgentConfigurationDocument(physicalPath, bytes, root);
        _documents.Add(physicalPath, document);
        return document;
    }

    public async Task<JsonObject?> ReadOptionalAsync(string path, CancellationToken cancellationToken)
        => (await ReadAsync(path, cancellationToken)).Root?.DeepClone().AsObject();

    public async Task<bool> IsCurrentAsync(CancellationToken cancellationToken)
    {
        foreach (var (alias, physicalPath) in _aliases)
        {
            if (!AgentConfigurationPath.Comparer.Equals(AgentConfigurationPath.Resolve(alias), physicalPath))
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
}

/// <summary>
/// A configuration snapshot read at application time, never during client discovery.
/// </summary>
internal sealed record AgentConfigurationDocument(string Path, byte[]? Bytes, JsonObject? Root);
