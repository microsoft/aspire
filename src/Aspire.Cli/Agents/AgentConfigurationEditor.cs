// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

internal static class AgentConfigurationEditor
{
    public static async Task<IReadOnlyList<AgentTargetResult>> ApplyAsync(
        IReadOnlyList<AgentFileEdit> edits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<AgentTargetResult>();
        var files = new Dictionary<string, List<AgentFileEdit>>(AgentPath.Comparer);
        foreach (var edit in edits)
        {
            try
            {
                var physicalPath = AgentPath.Resolve(edit.TargetPath);
                if (!files.TryGetValue(physicalPath, out var aliases))
                {
                    aliases = [];
                    files.Add(physicalPath, aliases);
                }

                aliases.Add(edit);
            }
            catch (AgentConfigurationException ex)
            {
                results.Add(new([edit.Client], edit.TargetPath, AgentConfigurationStatus.Blocked, ex.Message) { Error = ex });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                results.Add(new([edit.Client], edit.TargetPath, AgentConfigurationStatus.Failed,
                    string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.Configuration_ReadWriteFailed, ex.Message)) { Error = ex });
            }
        }

        foreach (var (physicalPath, targets) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = AgentConfigurationStatus.Configured;
            string? message = null;
            Exception? error = null;
            var changes = new List<(IAgentClient Client, string Path, AgentConfigurationEdit Edit)>();
            try
            {
                // A fresh reader lets later files observe earlier successful writes.
                // Aliases share one publication, but every consuming format must accept it.
                var reader = new AgentConfigurationJson.ReadContext();
                var document = await reader.ReadAsync(targets[0].TargetPath, targets[0].Parse, cancellationToken);
                if (!AgentPath.Comparer.Equals(document.Path, physicalPath))
                {
                    throw new AgentConfigurationException(AgentCommandStrings.Configuration_ConcurrentChange);
                }
                var root = document.Root?.DeepClone().AsObject() ?? new JsonObject();
                foreach (var target in targets.Skip(1))
                {
                    if (!AgentPath.Comparer.Equals(
                        (await reader.ReadAsync(target.TargetPath, target.Parse, cancellationToken)).Path, physicalPath))
                    {
                        throw new AgentConfigurationException(AgentCommandStrings.Configuration_ConcurrentChange);
                    }
                }

                foreach (var target in targets)
                {
                    var edit = await target.ApplyAsync(root, reader, cancellationToken);
                    if (edit.Status is not AgentConfigurationStatus.Configured)
                    {
                        status = edit.Status;
                        message = edit.Message;
                        break;
                    }

                    changes.Add((target.Client, target.TargetPath, edit));
                }

                if (status is AgentConfigurationStatus.Configured)
                {
                    if (JsonNode.DeepEquals(document.Root ?? new JsonObject(), root))
                    {
                        await reader.ValidateAsync(cancellationToken);
                        status = AgentConfigurationStatus.Unchanged;
                    }
                    else
                    {
                        await reader.SaveAsync(document, root, cancellationToken);
                    }
                }
            }
            catch (AgentConfigurationException ex)
            {
                status = AgentConfigurationStatus.Blocked;
                message = ex.Message;
                error = ex;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                status = AgentConfigurationStatus.Failed;
                message = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.Configuration_ReadWriteFailed, ex.Message);
                error = ex;
            }

            results.Add(new(targets.Select(target => target.Client).Distinct().ToArray(), targets[0].TargetPath, status, message)
            {
                Error = error,
                Changes = status is AgentConfigurationStatus.Configured or AgentConfigurationStatus.Unchanged ? changes.ToArray() : []
            });
        }

        return results;
    }
}

internal sealed record AgentFileEdit(
    IAgentClient Client,
    string TargetPath,
    Func<ReadOnlyMemory<byte>, JsonObject> Parse,
    Func<JsonObject, AgentConfigurationJson.ReadContext, CancellationToken, Task<AgentConfigurationEdit>> ApplyAsync);

internal sealed record AgentConfigurationEdit(AgentConfigurationStatus Status, string? Message)
{
    public bool Added { get; init; }
    public bool Repaired { get; init; }

    public static AgentConfigurationEdit Applied() => new(AgentConfigurationStatus.Configured, null);
    public static AgentConfigurationEdit Skipped(string message) => new(AgentConfigurationStatus.Skipped, message);
    public static AgentConfigurationEdit Blocked(string message) => new(AgentConfigurationStatus.Blocked, message);
}

internal sealed class AgentConfigurationException(string message) : Exception(message);

internal enum AgentConfigurationStatus
{
    Configured,
    Unchanged,
    Skipped,
    Blocked,
    Failed
}

internal sealed record AgentTargetResult(
    IReadOnlyList<IAgentClient> Clients,
    string TargetPath,
    AgentConfigurationStatus Status,
    string? Message)
{
    public Exception? Error { get; init; }
    public IReadOnlyList<(IAgentClient Client, string Path, AgentConfigurationEdit Edit)> Changes { get; init; } = [];
}
