// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Shared;

namespace Aspire.Cli.Agents;

internal sealed class AgentConfiguration
{
    private readonly Dictionary<string, byte[]?> _files = new(StringComparers.FileSystemPath);
    private readonly Dictionary<string, (bool Exists, string[] Files)> _directories = new(StringComparers.FileSystemPath);

    public static async Task<IReadOnlyList<AgentTargetResult>> ApplyRemoteAsync(
        IEnumerable<IAgentClient> clients, DirectoryInfo workspaceRoot, AgentConfigurationScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<AgentTargetResult>();
        foreach (var targets in clients.Distinct().GroupBy(client => Path.GetFullPath(client.GetIntegrationConfigPath(scope, workspaceRoot)), StringComparers.FileSystemPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = AgentConfigurationStatus.Configured;
            string? message = null;
            try
            {
                var edit = new AgentConfiguration();
                var original = await edit.ReadObjectForEditAsync(targets.Key, cancellationToken);
                var root = original?.DeepClone().AsObject() ?? new JsonObject();
                foreach (var client in targets)
                {
                    await client.UpdateIntegrationConfigAsync(root, workspaceRoot, scope, edit, cancellationToken);
                }

                if (JsonNode.DeepEquals(original ?? new JsonObject(), root))
                {
                    // No publication means no beforeReplace callback, so validate the inputs
                    // here rather than report success for settings or policy that changed during setup.
                    await edit.ValidateInputsUnchangedAsync(targets.Key, cancellationToken);
                    status = AgentConfigurationStatus.Unchanged;
                }
                else
                {
                    await edit.PublishAsync(targets.Key, root, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                status = AgentConfigurationStatus.Failed;
                message = ex.Message;
            }

            results.Add(new(targets.ToArray(), targets.Key, status, message));
        }

        return results;
    }

    public static JsonObject ParseObject(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: GetParseOptions());
        ArgumentNullException.ThrowIfNull(root);

        return root.AsObject();
    }

    public static JsonObject? ReadObject(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream;
        try
        {
            stream = File.OpenRead(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        using (stream)
        {
            try
            {
                var root = JsonNode.Parse(stream, documentOptions: GetParseOptions());
                ArgumentNullException.ThrowIfNull(root);

                return root.AsObject();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            {
                throw new InvalidOperationException($"{path}: {ex.Message}", ex);
            }
        }
    }

    private static JsonDocumentOptions GetParseOptions()
    {
        var options = ConfigurationHelper.ParseOptions;
        options.AllowDuplicateProperties = false;

        return options;
    }

    public static IEnumerable<string> EnumerateJsonFiles(string directory)
        => ReadDirectory(Path.GetFullPath(directory)).Files;

    public async Task<byte[]?> ReadBytesForEditAsync(string path, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        if (!_files.TryGetValue(path, out var content))
        {
            content = await ReadFileAsync(path, cancellationToken);
            _files.Add(path, content);
        }

        return content;
    }

    public async Task<JsonObject?> ReadObjectForEditAsync(string path, CancellationToken cancellationToken)
        => ParseFile(path, await ReadBytesForEditAsync(path, cancellationToken));

    public IEnumerable<string> GetJsonFilesForEdit(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!_directories.TryGetValue(directory, out var contents))
        {
            contents = ReadDirectory(directory);
            _directories.Add(directory, contents);
        }

        return contents.Files;
    }

    public async Task PublishAsync(string path, JsonObject settings, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_files.ContainsKey(path))
        {
            throw Refused(path);
        }
        ConfigurationHelper.ValidateSymbolicLinkPolicy(path, rejectSymbolicLinks: true);
        // All agent settings writers use the same sidecar, including MCP and user hooks.
        // Acquire only after read-only configuration validation, so refused setup creates no files.
        FileLock fileLock;
        try
        {
            fileLock = await FileLock.AcquireAsync(
                Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.aspire.lock"),
                cancellationToken, TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        using var lease = fileLock;
        await ConfigurationHelper.WriteSettingsFileAtomicallyAsync(
            path, settings, cancellationToken, rejectSymbolicLinks: true, preserveOwnershipAndAcl: true,
            beforeReplace: token => ValidateInputsUnchangedAsync(path, token));
    }

    private async Task ValidateInputsUnchangedAsync(string targetPath, CancellationToken cancellationToken)
    {
        foreach (var (path, content) in _files)
        {
            if (!string.Equals(path, targetPath, StringComparisons.FileSystemPath) &&
                !Equal(content, await ReadFileAsync(path, cancellationToken)))
            {
                throw Refused(path);
            }
        }

        // Check the destination last, not before awaiting inherited settings reads.
        if (_files.TryGetValue(targetPath, out var original) &&
            !Equal(original, await ReadFileAsync(targetPath, cancellationToken)))
        {
            throw Refused(targetPath);
        }

        foreach (var (path, previous) in _directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadDirectory(path);
            if (previous.Exists != current.Exists || !previous.Files.SequenceEqual(current.Files, StringComparers.FileSystemPath))
            {
                throw Refused(path);
            }
        }
    }

    private static JsonObject? ParseFile(string path, byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        try
        {
            // Parse the captured bytes, not a second file read, so validation tracks
            // the exact input used by the edit. Stream parsing also handles a UTF-8 BOM.
            using var stream = new MemoryStream(bytes, writable: false);
            var root = JsonNode.Parse(stream, documentOptions: GetParseOptions());
            ArgumentNullException.ThrowIfNull(root);

            return root.AsObject();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException($"{path}: {ex.Message}", ex);
        }
    }

    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static (bool Exists, string[] Files) ReadDirectory(string path)
    {
        try
        {
            return (true, Directory.EnumerateFiles(path, "*.json")
                .Where(file => !Path.GetFileName(file).StartsWith('.'))
                .Order(StringComparer.Ordinal).ToArray());
        }
        catch (DirectoryNotFoundException)
        {
            return (false, []);
        }
    }

    private static bool Equal(byte[]? left, byte[]? right)
        => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static IOException Refused(string path)
        => new(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.ConfigurationWriteRefused, path));
}

internal enum AgentConfigurationStatus
{
    Configured,
    Unchanged,
    Failed
}

internal sealed record AgentTargetResult(
    IReadOnlyList<IAgentClient> Clients,
    string TargetPath,
    AgentConfigurationStatus Status,
    string? Message);
