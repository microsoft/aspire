// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only
#pragma warning disable ASPIREUSERSECRETS001

using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines.Internal;
using Aspire.Shared.UserSecrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace Aspire.Hosting.UserSecrets;

/// <summary>
/// Factory for creating and caching <see cref="IUserSecretsManager"/> instances.
/// </summary>
/// <remarks>
/// Uses a lock to ensure thread-safe creation and a dictionary to cache instances by normalized file path.
/// </remarks>
internal sealed class UserSecretsManagerFactory
{
    // Dictionary to cache instances by file path
    private readonly Dictionary<string, IUserSecretsManager> _managerCache = new();
    private readonly object _lock = new();

    internal UserSecretsManagerFactory(IFileSystemService fileSystemService)
    {
        ArgumentNullException.ThrowIfNull(fileSystemService);
    }

    /// <summary>
    /// Gets or creates a user secrets manager for the specified file path.
    /// </summary>
    public IUserSecretsManager GetOrCreate(string filePath, string? legacyUserSecretsFilePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var normalizedPath = Path.GetFullPath(filePath);
        var normalizedLegacyPath = string.IsNullOrWhiteSpace(legacyUserSecretsFilePath) ? null : Path.GetFullPath(legacyUserSecretsFilePath);
        var cacheKey = normalizedLegacyPath is null ? normalizedPath : $"{normalizedPath}|{normalizedLegacyPath}";

        lock (_lock)
        {
            if (!_managerCache.TryGetValue(cacheKey, out var manager))
            {
                manager = new UserSecretsManager(normalizedPath, normalizedLegacyPath);
                _managerCache[cacheKey] = manager;
            }
            return manager;
        }
    }

    /// <summary>
    /// Gets or creates a user secrets manager for the specified user secrets ID.
    /// </summary>
    public IUserSecretsManager GetOrCreateFromId(string? userSecretsId)
    {
        if (string.IsNullOrWhiteSpace(userSecretsId))
        {
            return NoopUserSecretsManager.Instance;
        }

        var filePath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(userSecretsId);
        return GetOrCreate(filePath);
    }

    /// <summary>
    /// Gets or creates a user secrets manager for the assembly with UserSecretsIdAttribute.
    /// </summary>
    public IUserSecretsManager GetOrCreate(Assembly? assembly)
    {
        var userSecretsId = assembly?.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;
        return GetOrCreateFromId(userSecretsId);
    }

    private sealed class UserSecretsManager : IUserSecretsManager
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly string? _legacyUserSecretsFilePath;

        public UserSecretsManager(string filePath, string? legacyUserSecretsFilePath)
        {
            FilePath = filePath;
            _legacyUserSecretsFilePath = legacyUserSecretsFilePath;
        }

        public bool IsAvailable => true;

        public string FilePath { get; }

        public bool TrySetSecret(string name, string value)
        {
            try
            {
                _semaphore.Wait();
                try
                {
                    SetSecretCore(name, value);
                    return true;
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TryDeleteSecret(string name)
        {
            try
            {
                _semaphore.Wait();
                try
                {
                    DeleteSecretCore(FilePath, name);
                    if (_legacyUserSecretsFilePath is not null && !PathsEqual(_legacyUserSecretsFilePath, FilePath))
                    {
                        DeleteSecretCore(_legacyUserSecretsFilePath, name);
                    }
                    return true;
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void GetOrSetSecret(IConfigurationManager configuration, string name, Func<string> valueGenerator)
        {
            var existingValue = configuration[name];
            if (existingValue is null)
            {
                var value = valueGenerator();
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [name] = value
                    }
                );
                if (!TrySetSecret(name, value))
                {
                    Debug.WriteLine($"Failed to save value to application user secrets.");
                }
            }
        }

        /// <summary>
        /// Saves state to user secrets asynchronously (for deployment state manager).
        /// If multiple callers save state concurrently, the last write wins.
        /// </summary>
        public async Task SaveStateAsync(JsonObject state, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var flattenedState = JsonFlattener.FlattenJsonObject(state);
                var store = new SecretsStore(FilePath);
                store.Clear();
                foreach (var (key, value) in flattenedState)
                {
                    if (value is not null)
                    {
                        store.Set(key, value.ToString());
                    }
                }

                store.Save();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private void SetSecretCore(string name, string value)
        {
            var store = new SecretsStore(FilePath);
            store.Set(name, value);
            store.Save();
        }

        private static void DeleteSecretCore(string filePath, string name)
        {
            var store = new SecretsStore(filePath);
            if (store.Remove(name))
            {
                store.Save();
            }
        }

        private static bool PathsEqual(string left, string right) =>
            string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
