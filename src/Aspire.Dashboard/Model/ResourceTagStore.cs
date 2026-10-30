// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Dashboard.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Stores the tags users assign to resources.
/// </summary>
/// <remarks>
/// Tags describe the application rather than a single run, so they're stored in a settings database shared by every
/// run of the application instead of the per-run database, which is replaced on each run and pruned. Tags are keyed
/// by the resource's display name because replica names have a random suffix that changes on every run; a tag
/// therefore applies to every replica of a resource. When the dashboard doesn't persist data, tags are only kept in
/// memory.
/// </remarks>
public sealed class ResourceTagStore
{
    internal const int MaxTagLength = 50;
    private const string SettingsName = "resource tags";

    private readonly Lock _lock = new();
    private readonly DashboardSettingsDatabase _database;
    private readonly Dictionary<string, List<string>> _tagsByResource = new(StringComparers.ResourceName);
    private bool _loaded;

    public ResourceTagStore(IOptions<DashboardOptions> options, ILogger<ResourceTagStore> logger)
        : this(DashboardSettingsDatabase.GetDatabasePath(options.Value), logger)
    {
    }

    internal ResourceTagStore(string? databasePath, ILogger logger)
    {
        _database = new DashboardSettingsDatabase(databasePath, logger);
    }

    /// <summary>
    /// Gets the path of the settings database, or <see langword="null"/> when tags are only kept in memory.
    /// </summary>
    internal string? DatabasePath => _database.DatabasePath;

    /// <summary>
    /// Raised after tags are added or removed. Handlers can be invoked on any thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the tags of a resource in the order they were added.
    /// </summary>
    /// <param name="resourceName">The display name of the resource.</param>
    public IReadOnlyList<string> GetTags(string resourceName)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _tagsByResource.TryGetValue(resourceName, out var tags) ? tags.ToArray() : [];
        }
    }

    /// <summary>
    /// Gets every tag assigned to at least one resource, ordered by name.
    /// </summary>
    public IReadOnlyList<string> GetAllTags()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return GetAllTagsCore().Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    /// <summary>
    /// Gets a snapshot of the tags of every tagged resource, keyed by resource display name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetTagsByResource()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _tagsByResource.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value.ToArray(), StringComparers.ResourceName);
        }
    }

    /// <summary>Gets editable tags merged with immutable tags in the selected run's resource snapshots.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetTagsByResource(IEnumerable<ResourceViewModel> resources)
    {
        var tagsByResource = new Dictionary<string, IReadOnlyList<string>>(GetTagsByResource(), StringComparers.ResourceName);
        foreach (var resource in resources)
        {
            var appHostTags = resource.AppHostTags;
            if (appHostTags.Count > 0)
            {
                tagsByResource[resource.DisplayName] = appHostTags
                    .Concat(tagsByResource.GetValueOrDefault(resource.DisplayName) ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        return tagsByResource;
    }

    public IReadOnlyList<string> GetAllTags(IEnumerable<ResourceViewModel> resources) =>
        GetTagsByResource(resources).Values.SelectMany(tags => tags).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();

    /// <summary>
    /// Adds a tag to a resource. A tag that matches an existing tag ignoring case reuses the existing tag's casing.
    /// </summary>
    /// <param name="resourceName">The display name of the resource.</param>
    /// <param name="tag">The tag to add.</param>
    /// <returns><see langword="true"/> when the tag was added; otherwise, <see langword="false"/> when the tag is empty or already assigned.</returns>
    public bool AddTag(string resourceName, string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        if (NormalizeTag(tag) is not { } normalizedTag)
        {
            return false;
        }

        lock (_lock)
        {
            EnsureLoaded();

            // Reusing the casing of an existing tag keeps a single group per tag in the tag list.
            normalizedTag = GetAllTagsCore().FirstOrDefault(t => string.Equals(t, normalizedTag, StringComparison.OrdinalIgnoreCase)) ?? normalizedTag;

            if (!_tagsByResource.TryGetValue(resourceName, out var tags))
            {
                tags = [];
                _tagsByResource[resourceName] = tags;
            }
            else if (tags.Contains(normalizedTag, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            tags.Add(normalizedTag);
            Persist(command =>
            {
                command.CommandText = "INSERT OR IGNORE INTO resource_tags (resource_name, tag) VALUES ($resourceName, $tag);";
                command.Parameters.AddWithValue("$resourceName", resourceName);
                command.Parameters.AddWithValue("$tag", normalizedTag);
            });
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Removes a tag from a resource.
    /// </summary>
    /// <param name="resourceName">The display name of the resource.</param>
    /// <param name="tag">The tag to remove.</param>
    /// <returns><see langword="true"/> when the tag was removed; otherwise, <see langword="false"/>.</returns>
    public bool RemoveTag(string resourceName, string tag)
    {
        lock (_lock)
        {
            EnsureLoaded();

            if (!_tagsByResource.TryGetValue(resourceName, out var tags) ||
                tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return false;
            }

            if (tags.Count == 0)
            {
                _tagsByResource.Remove(resourceName);
            }

            Persist(command =>
            {
                command.CommandText = "DELETE FROM resource_tags WHERE resource_name = $resourceName AND tag = $tag;";
                command.Parameters.AddWithValue("$resourceName", resourceName);
                command.Parameters.AddWithValue("$tag", tag);
            });
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Trims a tag and collapses inner whitespace so visually identical tags are equal.
    /// </summary>
    /// <returns>The normalized tag, or <see langword="null"/> when the tag is empty.</returns>
    internal static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var builder = new StringBuilder(tag.Length);
        var pendingSpace = false;
        foreach (var character in tag.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        var normalized = builder.ToString();
        return normalized.Length > MaxTagLength ? normalized[..MaxTagLength].TrimEnd() : normalized;
    }

    private IEnumerable<string> GetAllTagsCore() =>
        _tagsByResource.Values.SelectMany(tags => tags).Distinct(StringComparer.OrdinalIgnoreCase);

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _database.Load(SettingsName, connection =>
        {
            using (var createCommand = connection.CreateCommand())
            {
                // The tag column uses NOCASE so a tag can't be stored twice for a resource with different casing.
                createCommand.CommandText = """
                    CREATE TABLE IF NOT EXISTS resource_tags (
                        resource_name TEXT NOT NULL COLLATE NOCASE,
                        tag TEXT NOT NULL COLLATE NOCASE,
                        PRIMARY KEY (resource_name, tag)
                    );
                    """;
                createCommand.ExecuteNonQuery();
            }

            using var selectCommand = connection.CreateCommand();
            // rowid preserves the order tags were added in.
            selectCommand.CommandText = "SELECT resource_name, tag FROM resource_tags ORDER BY rowid;";
            using var reader = selectCommand.ExecuteReader();
            while (reader.Read())
            {
                var resourceName = reader.GetString(0);
                if (!_tagsByResource.TryGetValue(resourceName, out var tags))
                {
                    tags = [];
                    _tagsByResource[resourceName] = tags;
                }

                tags.Add(reader.GetString(1));
            }
        });
    }

    private void Persist(Action<SqliteCommand> configureCommand)
    {
        // A tag that couldn't be saved is still shown for the rest of the run. Tags are a convenience, so it isn't
        // worth failing the edit for.
        _database.TrySave(SettingsName, connection =>
        {
            using var command = connection.CreateCommand();
            configureCommand(command);
            command.ExecuteNonQuery();
        });
    }
}
