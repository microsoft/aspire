// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.IO.Hashing;
using System.Text.Json;
using Azure.AI.Projects.Agents;

namespace Aspire.Hosting.Foundry;

internal sealed class FoundryToolboxDeploymentDefinition
{
    internal const string ManagedByMetadataKey = "aspire-managed-by";
    internal const string ManagedByMetadataValue = "Aspire.Hosting.Foundry";
    internal const string ConfigurationHashMetadataKey = "aspire-configuration-hash";
    internal const string SchemaVersionMetadataKey = "aspire-schema-version";

    private const int MaximumMetadataEntries = 16;
    private static readonly string[] s_reservedMetadataKeys =
    [
        ManagedByMetadataKey,
        ConfigurationHashMetadataKey,
        SchemaVersionMetadataKey
    ];

    private FoundryToolboxDeploymentDefinition(
        string name,
        string description,
        IReadOnlyList<ResolvedFoundryToolboxTool> tools,
        IReadOnlyDictionary<string, string> metadata,
        string configurationHash)
    {
        Name = name;
        Description = description;
        Tools = tools;
        Metadata = metadata;
        ConfigurationHash = configurationHash;
    }

    public string Name { get; }

    public string Description { get; }

    public IReadOnlyList<ResolvedFoundryToolboxTool> Tools { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public string ConfigurationHash { get; }

    public static FoundryToolboxDeploymentDefinition Create(
        string name,
        string description,
        IReadOnlyList<ResolvedFoundryToolboxTool> tools,
        IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(metadata);

        if (tools.Count == 0)
        {
            throw new InvalidOperationException($"Toolbox '{name}' must contain at least one tool.");
        }

        var duplicateToolNames = tools
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (duplicateToolNames.Length > 0)
        {
            throw new InvalidOperationException(
                $"Toolbox '{name}' contains duplicate tool names: {string.Join(", ", duplicateToolNames)}.");
        }

        var maximumUserMetadataEntries = MaximumMetadataEntries - s_reservedMetadataKeys.Length;
        if (metadata.Count > maximumUserMetadataEntries)
        {
            throw new InvalidOperationException(
                $"Toolbox '{name}' supports at most {maximumUserMetadataEntries} user metadata entries.");
        }

        foreach (var reservedKey in s_reservedMetadataKeys)
        {
            if (metadata.ContainsKey(reservedKey))
            {
                throw new InvalidOperationException(
                    $"Toolbox metadata key '{reservedKey}' is reserved for Aspire.");
            }
        }

        var configurationHash = ComputeConfigurationHash(description, tools, metadata);
        return new(name, description, tools, metadata, configurationHash);
    }

    public IDictionary<string, string> CreateDeploymentMetadata()
    {
        var metadata = new Dictionary<string, string>(Metadata, StringComparer.Ordinal)
        {
            [ManagedByMetadataKey] = ManagedByMetadataValue,
            [ConfigurationHashMetadataKey] = ConfigurationHash,
            [SchemaVersionMetadataKey] = "1"
        };

        return metadata;
    }

    private static string ComputeConfigurationHash(
        string description,
        IReadOnlyList<ResolvedFoundryToolboxTool> tools,
        IReadOnlyDictionary<string, string> metadata)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("description", description);
            writer.WriteStartObject("metadata");
            foreach (var item in metadata.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.WriteString(item.Key, item.Value);
            }
            writer.WriteEndObject();
            writer.WriteStartArray("tools");
            foreach (var tool in tools.OrderBy(tool => tool.Name, StringComparer.Ordinal))
            {
                writer.WriteRawValue(tool.CanonicalConfiguration);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var hash = XxHash3.Hash(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal sealed record ResolvedFoundryToolboxTool(
    string Name,
    ProjectsAgentTool Tool,
    string CanonicalConfiguration);

internal sealed record FoundryToolboxState(
    string DefaultVersion,
    IReadOnlyList<FoundryToolboxVersionState> Versions)
{
    public FoundryToolboxVersionState Default =>
        Versions.Single(version => string.Equals(version.Version, DefaultVersion, StringComparison.Ordinal));
}

internal sealed record FoundryToolboxVersionState(
    string Version,
    IReadOnlyDictionary<string, string> Metadata);

internal interface IFoundryToolboxAdministration
{
    Task<FoundryToolboxState?> GetAsync(string name, CancellationToken cancellationToken);

    Task<string> CreateVersionAsync(
        FoundryToolboxDeploymentDefinition definition,
        CancellationToken cancellationToken);

    Task PromoteVersionAsync(string name, string version, CancellationToken cancellationToken);
}

internal sealed class AzureFoundryToolboxAdministration(
    AgentToolboxes toolboxes,
    Action<string> logRetry) : IFoundryToolboxAdministration
{
    private const int ProjectEndpointReadinessMaxRetryAttempts = 11;
    private static readonly TimeSpan s_projectEndpointReadinessDelay = TimeSpan.FromSeconds(5);

    public async Task<FoundryToolboxState?> GetAsync(string name, CancellationToken cancellationToken)
    {
        ToolboxRecord toolbox;
        try
        {
            toolbox = await ExecuteWithProjectReadinessRetryAsync(
                async token => (await toolboxes.GetToolboxAsync(name, token).ConfigureAwait(false)).Value,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ClientResultException ex) when (ex.Status == 404 && !IsProjectEndpointNotReady(ex))
        {
            return null;
        }

        var versions = await ExecuteWithProjectReadinessRetryAsync(
            async token =>
            {
                var result = new List<FoundryToolboxVersionState>();
                await foreach (var version in toolboxes.GetToolboxVersionsAsync(
                    name,
                    cancellationToken: token).ConfigureAwait(false))
                {
                    result.Add(new(
                        version.Version,
                        new Dictionary<string, string>(version.Metadata, StringComparer.Ordinal)));
                }

                return result;
            },
            cancellationToken).ConfigureAwait(false);

        if (!versions.Any(version =>
            string.Equals(version.Version, toolbox.DefaultVersion, StringComparison.Ordinal)))
        {
            var defaultVersion = await ExecuteWithProjectReadinessRetryAsync(
                async token => (await toolboxes.GetToolboxVersionAsync(
                    name,
                    toolbox.DefaultVersion,
                    token).ConfigureAwait(false)).Value,
                cancellationToken).ConfigureAwait(false);
            versions.Add(new(
                defaultVersion.Version,
                new Dictionary<string, string>(defaultVersion.Metadata, StringComparer.Ordinal)));
        }

        return new(toolbox.DefaultVersion, versions);
    }

    public Task<string> CreateVersionAsync(
        FoundryToolboxDeploymentDefinition definition,
        CancellationToken cancellationToken)
    {
        return ExecuteWithProjectReadinessRetryAsync(
            async token =>
            {
                var result = await toolboxes.CreateToolboxVersionAsync(
                    definition.Name,
                    definition.Tools.Select(tool => tool.Tool),
                    definition.Description,
                    definition.CreateDeploymentMetadata(),
                    policies: null,
                    token).ConfigureAwait(false);
                return result.Value.Version;
            },
            cancellationToken);
    }

    public async Task PromoteVersionAsync(
        string name,
        string version,
        CancellationToken cancellationToken)
    {
        await ExecuteWithProjectReadinessRetryAsync(
            async token =>
            {
                var options = new RequestOptions
                {
                    CancellationToken = token
                };
                await toolboxes.UpdateToolboxAsync(name, version, options).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsProjectEndpointNotReady(ClientResultException ex) =>
        ex.Status == 404 &&
        (ex.Message.Contains("Subdomain does not map to a resource", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("The project does not exist", StringComparison.OrdinalIgnoreCase));

    private async Task<T> ExecuteWithProjectReadinessRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (ClientResultException ex)
                when (IsProjectEndpointNotReady(ex) &&
                    attempt < ProjectEndpointReadinessMaxRetryAttempts)
            {
                logRetry(
                    $"Foundry project endpoint is not ready. Retrying toolbox deployment in {s_projectEndpointReadinessDelay.TotalSeconds:n0} seconds ({attempt + 1}/{ProjectEndpointReadinessMaxRetryAttempts}).");
                await Task.Delay(s_projectEndpointReadinessDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

internal sealed class FoundryToolboxReconciler(IFoundryToolboxAdministration administration)
{
    public async Task<FoundryToolboxReconcileResult> ReconcileAsync(
        FoundryToolboxDeploymentDefinition definition,
        CancellationToken cancellationToken)
    {
        var existing = await administration.GetAsync(definition.Name, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var created = await administration.CreateVersionAsync(definition, cancellationToken).ConfigureAwait(false);
            return new(created, FoundryToolboxReconcileAction.Created);
        }

        if (!existing.Default.Metadata.TryGetValue(
                FoundryToolboxDeploymentDefinition.ManagedByMetadataKey,
                out var managedBy) ||
            !string.Equals(
                managedBy,
                FoundryToolboxDeploymentDefinition.ManagedByMetadataValue,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Toolbox '{definition.Name}' already exists but is not managed by Aspire. No changes were made.");
        }

        if (existing.Default.Metadata.TryGetValue(
                FoundryToolboxDeploymentDefinition.ConfigurationHashMetadataKey,
                out var existingHash) &&
            string.Equals(existingHash, definition.ConfigurationHash, StringComparison.Ordinal))
        {
            return new(existing.DefaultVersion, FoundryToolboxReconcileAction.Reused);
        }

        var reusableVersion = existing.Versions.FirstOrDefault(version =>
            version.Metadata.TryGetValue(
                FoundryToolboxDeploymentDefinition.ManagedByMetadataKey,
                out var versionManagedBy) &&
            string.Equals(
                versionManagedBy,
                FoundryToolboxDeploymentDefinition.ManagedByMetadataValue,
                StringComparison.Ordinal) &&
            version.Metadata.TryGetValue(
                FoundryToolboxDeploymentDefinition.ConfigurationHashMetadataKey,
                out var versionHash) &&
            string.Equals(versionHash, definition.ConfigurationHash, StringComparison.Ordinal));
        if (reusableVersion is not null)
        {
            await administration.PromoteVersionAsync(
                definition.Name,
                reusableVersion.Version,
                cancellationToken).ConfigureAwait(false);
            return new(reusableVersion.Version, FoundryToolboxReconcileAction.Promoted);
        }

        var updated = await administration.CreateVersionAsync(definition, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(existing.DefaultVersion, updated, StringComparison.Ordinal))
        {
            await administration.PromoteVersionAsync(
                definition.Name,
                updated,
                cancellationToken).ConfigureAwait(false);
        }

        return new(updated, FoundryToolboxReconcileAction.CreatedAndPromoted);
    }
}

internal sealed record FoundryToolboxReconcileResult(
    string Version,
    FoundryToolboxReconcileAction Action);

internal enum FoundryToolboxReconcileAction
{
    Created,
    Reused,
    Promoted,
    CreatedAndPromoted
}
