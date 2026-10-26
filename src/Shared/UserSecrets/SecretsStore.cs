// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Shared.Json;

namespace Aspire.Shared.UserSecrets;

/// <summary>
/// Provides CRUD operations over a dotnet user-secrets JSON file.
/// </summary>
internal sealed class SecretsStore
{
    private readonly string _secretsFilePath;
    private readonly Dictionary<string, JsonNode?> _secrets;

    /// <summary>
    /// Creates a new SecretsStore backed by the specified file path.
    /// </summary>
    public SecretsStore(string secretsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsFilePath);

        _secretsFilePath = secretsFilePath;
        _secrets = Load(secretsFilePath);
    }

    /// <summary>
    /// Gets the file path of the secrets file.
    /// </summary>
    public string FilePath => _secretsFilePath;

    /// <summary>
    /// Gets the number of secrets in the store.
    /// </summary>
    public int Count => _secrets.Count(entry => entry.Value is not null);

    /// <summary>
    /// Sets a secret value. Call <see cref="Save"/> to persist.
    /// </summary>
    public void Set(string key, string value) => _secrets[key] = value;

    /// <summary>
    /// Gets a secret value by key, or null if not found.
    /// </summary>
    public string? Get(string key) => _secrets.GetValueOrDefault(key)?.ToString();

    /// <summary>
    /// Removes a secret by key. Call <see cref="Save"/> to persist.
    /// </summary>
    public bool Remove(string key) => _secrets.Remove(key);

    /// <summary>
    /// Returns true if the store contains the specified key.
    /// </summary>
    public bool ContainsKey(string key) => _secrets.ContainsKey(key);

    /// <summary>
    /// Removes all secrets from the store. Call <see cref="Save"/> to persist.
    /// </summary>
    public void Clear() => _secrets.Clear();

    /// <summary>
    /// Returns all secret key-value pairs.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> AsEnumerable()
    {
        foreach (var (key, value) in _secrets)
        {
            if (value is not null)
            {
                yield return KeyValuePair.Create(key, value.ToString());
            }
        }
    }

    /// <summary>
    /// Returns all secret key-value pairs as a list.
    /// </summary>
    public List<KeyValuePair<string, string>> ToList() => [.. AsEnumerable()];

    /// <summary>
    /// Imports missing values without changing existing values or their JSON types.
    /// </summary>
    public void MergeFrom(SecretsStore source)
    {
        foreach (var (key, value) in source._secrets)
        {
            if (!_secrets.ContainsKey(key))
            {
                _secrets.Add(key, value?.DeepClone());
            }
        }
    }

    /// <summary>
    /// Persists the current secrets to disk.
    /// </summary>
    public void Save()
    {
        var obj = new JsonObject();
        foreach (var (key, value) in _secrets)
        {
            obj[key] = value?.DeepClone();
        }

        SaveJson(_secretsFilePath, obj);
    }

    /// <summary>
    /// Atomically persists JSON without converting its values to strings.
    /// </summary>
    internal static void SaveJson(string filePath, JsonObject obj)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                // Explicit secrets files can live in caller-owned shared directories.
                // Apply private permissions only when creating directories, never chmod existing parents.
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var json = obj.ToJsonString(UserSecretsJsonOptions.s_instance);

        if (!string.IsNullOrEmpty(directory))
        {
            var tempFilename = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None
                };

                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var stream = new FileStream(tempFilename, options))
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(tempFilename, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }

                    using var writer = new StreamWriter(stream);
                    writer.Write(json);
                }

                if (OperatingSystem.IsWindows() && File.Exists(filePath))
                {
                    // Replacement preserves the destination DACL; a move would inherit the
                    // temporary file's permissions from a potentially broader-access directory.
                    File.Replace(tempFilename, filePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempFilename, filePath, overwrite: true);
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            finally
            {
                if (File.Exists(tempFilename))
                {
                    File.Delete(tempFilename);
                }
            }
        }
        else
        {
            File.WriteAllText(filePath, json);
        }
    }

    /// <summary>
    /// Loads secrets from a JSON file, flattening any nested structure to colon-separated keys.
    /// </summary>
    private static Dictionary<string, JsonNode?> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        }

        var parsed = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        })?.AsObject();
        if (parsed is null)
        {
            return new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        }

        // Flatten nested JSON to colon-separated keys
        var flat = JsonFlattener.FlattenJsonObject(parsed);

        // The same file also holds deployment state. Keep raw nodes for persistence;
        // only the string-facing CRUD and listing APIs should stringify values.
        var result = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in flat)
        {
            result[kvp.Key] = kvp.Value;
        }

        return result;
    }
}
