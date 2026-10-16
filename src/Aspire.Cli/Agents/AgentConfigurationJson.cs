// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Agents;

/// <summary>
/// Native JSON shape checks and input tracking for agent configuration edits.
/// </summary>
internal static class AgentConfigurationJson
{
    public static JsonObject ParseObject(ReadOnlySpan<byte> content)
    {
        try
        {
            // Files and inline settings use the same UTF-8 JSONC contract. A BOM is
            // accepted at the boundary; callers retain the original bytes for no-ops.
            if (content.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                content = content[3..];
            }

            var parseOptions = ConfigurationHelper.ParseOptions;
            parseOptions.AllowDuplicateProperties = false;
            var root = JsonNode.Parse(content, documentOptions: parseOptions) as JsonObject
                ?? throw Shape("$");

            return root;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new AgentConfigurationException(AgentCommandStrings.Configuration_MalformedJson);
        }
    }

    public static JsonObject? OptionalObject(JsonObject parent, string key)
        => !parent.TryGetPropertyValue(key, out var node) ? null : node as JsonObject ?? throw Shape(key);

    public static JsonObject Object(JsonObject parent, string key)
    {
        var value = OptionalObject(parent, key);
        if (value is null)
        {
            value = new JsonObject();
            parent[key] = value;
        }

        return value;
    }

    public static JsonArray? OptionalStrings(JsonObject parent, string key)
    {
        if (!parent.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        if (node is not JsonArray array || array.Any(value => String(value) is null))
        {
            throw Shape(key);
        }

        return array;
    }

    public static string? String(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool? Boolean(JsonObject parent, string key)
    {
        if (!parent.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<bool>(out var enabled) ? enabled : throw Shape(key);
    }

    public static AgentConfigurationException Shape(string key)
        => new(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.Configuration_UnexpectedShape, key));

    public static async Task<IReadOnlyList<JsonObject>> ReadSettingsAsync(
        ReadContext context,
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var settings = new List<JsonObject>();
        foreach (var path in paths.Distinct(AgentPath.Comparer))
        {
            if (await context.ReadOptionalAsync(path, cancellationToken) is { } root)
            {
                settings.Add(root);
            }
        }

        return settings;
    }

    internal sealed class ReadContext
    {
        private readonly Dictionary<string, Document> _documents = new(AgentPath.Comparer);
        private readonly Dictionary<string, string> _aliases = new(AgentPath.Comparer);

        public Task<Document> ReadAsync(string path, CancellationToken cancellationToken)
            => ReadAsync(path, static bytes => ParseObject(bytes.Span), cancellationToken);

        public async Task<Document> ReadAsync(
            string path, Func<ReadOnlyMemory<byte>, JsonObject> parse, CancellationToken cancellationToken)
        {
            var document = await ReadFileAsync(path, cancellationToken);
            try
            {
                // Cache observations, not interpretations: aliased consumers must each
                // validate the original bytes under their own format's parsing contract.
                return document with { Root = document.Bytes is { } bytes ? parse(bytes) : null };
            }
            catch (AgentConfigurationException ex)
            {
                throw new AgentConfigurationException($"{document.Path}: {ex.Message}");
            }
        }

        public async Task<Document> ReadFileAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var physicalPath = AgentPath.Resolve(path);
            if (_aliases.TryGetValue(path, out var previousPath) && !AgentPath.Comparer.Equals(previousPath, physicalPath))
            {
                throw new AgentConfigurationException(AgentCommandStrings.Configuration_ConcurrentChange);
            }

            _aliases[path] = physicalPath;
            if (_documents.TryGetValue(physicalPath, out var existing))
            {
                return existing;
            }

            var bytes = await AgentFileWriter.ReadExistingAsync(physicalPath, cancellationToken);
            var document = new Document(physicalPath, bytes, null);
            _documents.Add(physicalPath, document);
            return document;
        }

        public async Task<JsonObject?> ReadOptionalAsync(string path, CancellationToken cancellationToken)
            => (await ReadAsync(path, cancellationToken)).Root;

        public async Task ValidateAsync(CancellationToken cancellationToken)
        {
            foreach (var (alias, physicalPath) in _aliases)
            {
                if (!AgentPath.Comparer.Equals(AgentPath.Resolve(alias), physicalPath))
                {
                    throw new AgentConfigurationException(AgentCommandStrings.Configuration_ConcurrentChange);
                }
            }

            foreach (var document in _documents.Values)
            {
                var current = await AgentFileWriter.ReadExistingAsync(document.Path, cancellationToken);
                if (document.Bytes is null ? current is not null : current is null || !document.Bytes.AsSpan().SequenceEqual(current))
                {
                    throw new AgentConfigurationException(AgentCommandStrings.Configuration_ConcurrentChange);
                }
            }
        }

        public Task SaveAsync(Document document, JsonObject root, CancellationToken cancellationToken)
            => AgentFileWriter.WriteAsync(document, root, this, cancellationToken);

        internal sealed record Document(string Path, byte[]? Bytes, JsonObject? Root);
    }
}
