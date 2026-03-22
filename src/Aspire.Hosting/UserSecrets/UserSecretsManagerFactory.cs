// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only
#pragma warning disable ASPIREUSERSECRETS001

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
    private readonly IFileSystemService _fileSystemService;

    internal UserSecretsManagerFactory(IFileSystemService fileSystemService)
    {
        _fileSystemService = fileSystemService;
    }

    /// <summary>
    /// Gets or creates a user secrets manager for the specified file path.
    /// </summary>
    public IUserSecretsManager GetOrCreate(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var normalizedPath = Path.GetFullPath(filePath);

        lock (_lock)
        {
            if (!_managerCache.TryGetValue(normalizedPath, out var manager))
            {
                manager = new UserSecretsManager(normalizedPath, _fileSystemService);
                _managerCache[normalizedPath] = manager;
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
        private static readonly JsonSerializerOptions s_jsonSerializerOptions = new() { WriteIndented = true };

        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly IFileSystemService _fileSystemService;

        public UserSecretsManager(string filePath, IFileSystemService fileSystemService)
        {
            FilePath = filePath;
            _fileSystemService = fileSystemService;
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
                EnsureUserSecretsDirectory();

                var json = flattenedState.ToJsonString(s_jsonSerializerOptions);
                await File.WriteAllTextAsync(FilePath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private void SetSecretCore(string name, string value)
        {
            EnsureUserSecretsDirectory();

            // Load existing secrets, merge with new value, save
            var secrets = Load();
            secrets[name] = value;
            Save(secrets);
        }

        private Dictionary<string, string?> Load()
        {
            return new ConfigurationBuilder()
                .AddJsonFile(FilePath, optional: true)
                .Build()
                .AsEnumerable()
                .Where(i => i.Value != null)
                .ToDictionary(i => i.Key, i => i.Value);
        }

        private void Save(Dictionary<string, string?> secrets)
        {
            var contents = new JsonObject();
            foreach (var secret in secrets)
            {
                contents[secret.Key] = secret.Value;
            }

            var json = contents.ToJsonString(s_jsonSerializerOptions);

            // Create a temp file with the correct Unix file mode before moving it to the expected path.
            if (!OperatingSystem.IsWindows())
            {
                var tempFilename = _fileSystemService.TempDirectory.CreateTempFile().Path;
                File.WriteAllText(tempFilename, json, Encoding.UTF8);
                File.Move(tempFilename, FilePath, overwrite: true);
            }
            else
            {
                File.WriteAllText(FilePath, json, Encoding.UTF8);
            }
        }

        private void EnsureUserSecretsDirectory()
        {
            var directoryName = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
            {
                Directory.CreateDirectory(directoryName);
            }
        }
    }
}
