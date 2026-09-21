// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.Configuration;

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
        var plan = AgentConfigurationPlanner.Group(targets);
        var results = new List<AgentTargetResult>(plan.Results);
        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new List<AgentTargetResult>();
            try
            {
                // A reader belongs to one commit, so a later file observes earlier successful
                // writes rather than reusing a discovery snapshot or our own stale policy reads.
                var reader = new AgentConfigurationReadContext();
                var document = await reader.ReadAsync(file.Path, cancellationToken);
                foreach (var alias in file.Aliases)
                {
                    if (!AgentConfigurationPath.Comparer.Equals((await reader.ReadAsync(alias, cancellationToken)).Path, file.Path))
                    {
                        throw new AgentConfigurationException(AgentConfigurationStrings.ConcurrentChange);
                    }
                }

                var root = document.Root?.DeepClone().AsObject() ?? new JsonObject();
                foreach (var target in file.Targets)
                {
                    var candidate = root.DeepClone().AsObject();
                    try
                    {
                        var edit = await target.ApplyAsync(candidate, new AgentConfigurationMutationContext(reader, results, pending), cancellationToken);
                        var status = edit.Status;
                        if (status is AgentConfigurationStatus.Configured)
                        {
                            status = JsonNode.DeepEquals(root, candidate) ? AgentConfigurationStatus.Unchanged : AgentConfigurationStatus.Configured;
                            root = candidate;
                        }

                        pending.Add(AgentConfigurationPlanner.ToResult(target, status, edit.Message));
                    }
                    catch (AgentConfigurationException ex)
                    {
                        pending.Add(AgentConfigurationPlanner.ToResult(target, AgentConfigurationStatus.Blocked, ex.Message));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogDebug(ex, "Could not prepare agent configuration at {Path}.", target.Path);
                        pending.Add(AgentConfigurationPlanner.ToResult(target, AgentConfigurationStatus.Failed,
                            string.Format(CultureInfo.CurrentCulture, AgentConfigurationStrings.ReadWriteFailed, ex.Message)));
                    }
                }

                if (!JsonNode.DeepEquals(document.Root ?? new JsonObject(), root))
                {
                    await SaveAsync(document, root, reader, cancellationToken);
                }
                else if (!await reader.IsCurrentAsync(cancellationToken))
                {
                    throw new AgentConfigurationException(AgentConfigurationStrings.ConcurrentChange);
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
                    string.Format(CultureInfo.CurrentCulture, AgentConfigurationStrings.ReadWriteFailed, ex.Message), stale: false);
            }

            results.AddRange(pending);
        }

        return results;
    }

    private static async Task SaveAsync(
        AgentConfigurationDocument document,
        JsonObject root,
        AgentConfigurationReadContext reader,
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
                    throw new AgentConfigurationException(AgentConfigurationStrings.ConcurrentChange);
                }
            },
            newFileMode: UnixFileMode.UserRead | UnixFileMode.UserWrite,
            cancellationToken);
    }

    private static void CompleteFailure(
        AgentConfigurationFile file,
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
            pending.Add(AgentConfigurationPlanner.ToResult(target, status, message));
        }
    }
}
