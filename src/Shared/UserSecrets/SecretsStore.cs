// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Shared.Json;

namespace Aspire.Shared.UserSecrets;

/// <summary>
/// Provides CRUD operations over a dotnet user-secrets JSON file.
/// </summary>
internal sealed class SecretsStore
{
    internal static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _secretsFilePath;
    private readonly Dictionary<string, string> _secrets;

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
    public int Count => _secrets.Count;

    /// <summary>
    /// Sets a secret value. Call <see cref="Save"/> to persist.
    /// </summary>
    public void Set(string key, string value) => _secrets[key] = value;

    /// <summary>
    /// Gets a secret value by key, or null if not found.
    /// </summary>
    public string? Get(string key) => _secrets.GetValueOrDefault(key);

    /// <summary>
    /// Removes a secret by key. Call <see cref="Save"/> to persist.
    /// </summary>
    public bool Remove(string key) => _secrets.Remove(key);

    /// <summary>
    /// Returns true if the store contains the specified key.
    /// </summary>
    public bool ContainsKey(string key) => _secrets.ContainsKey(key);

    /// <summary>
    /// Returns all secret key-value pairs.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> AsEnumerable() => _secrets;

    /// <summary>
    /// Returns all secret key-value pairs as a list.
    /// </summary>
    public List<KeyValuePair<string, string>> ToList() => [.. _secrets];

    /// <summary>
    /// Persists the current secrets to disk.
    /// </summary>
    public void Save()
    {
        var directory = Path.GetDirectoryName(_secretsFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var obj = new JsonObject();
        foreach (var (key, value) in _secrets)
        {
            obj[key] = value;
        }

        var json = obj.ToJsonString(s_jsonOptions);

        // Unix: write to temp file then move for atomicity (matches aspnetcore pattern)
        if (!OperatingSystem.IsWindows())
        {
            var tempFilename = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFilename, json);
                File.Move(tempFilename, _secretsFilePath, overwrite: true);
            }
            finally
            {
                // Clean up temp file if move failed
                if (File.Exists(tempFilename))
                {
                    File.Delete(tempFilename);
                }
            }
        }
        else
        {
            File.WriteAllText(_secretsFilePath, json);
        }
    }

    /// <summary>
    /// Loads secrets from a JSON file, flattening any nested structure to colon-separated keys.
    /// </summary>
    private static Dictionary<string, string> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var parsed = JsonNode.Parse(json)?.AsObject();
        if (parsed is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Flatten nested JSON to colon-separated keys
        var flat = JsonFlattener.FlattenJsonObject(parsed);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in flat)
        {
            var value = kvp.Value?.ToString();
            if (value is not null)
            {
                result[kvp.Key] = value;
            }
        }

        return result;
    }
}
