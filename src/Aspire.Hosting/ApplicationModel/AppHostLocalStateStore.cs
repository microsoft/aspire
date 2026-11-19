// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

internal sealed class AppHostLocalStateStore
{
    // State updates only hold the lock for a small local JSON read/write. Persistence is best-effort,
    // so use a short timeout rather than allowing the shared FileLock default to delay AppHost startup.
    private static readonly TimeSpan s_lockTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<AppHostLocalStateStore> _logger;
    private readonly TimeSpan _lockTimeout;
    private readonly string? _stateDirectory;
    private readonly string? _statePath;
    private readonly string? _lockPath;
    private int _diskAccessDisabled;

    public AppHostLocalStateStore(IConfiguration configuration, ILogger<AppHostLocalStateStore> logger)
        : this(configuration, logger, s_lockTimeout)
    {
    }

    internal AppHostLocalStateStore(
        IConfiguration configuration,
        ILogger<AppHostLocalStateStore> logger,
        TimeSpan lockTimeout)
    {
        _logger = logger;
        _lockTimeout = lockTimeout;

        if (configuration["AppHost:Directory"] is not { Length: > 0 } appHostDirectory)
        {
            return;
        }

        // The deployment-state identity is derived from the AppHost path and is stable across
        // direct and CLI launches. DCP workload IDs are launcher-specific, so only use one as
        // a fallback when the AppHost path identities are unavailable.
        var identity = configuration["AppHost:DeploymentStatePathSha256"]
            ?? configuration["AppHost:FilePath"]
            ?? configuration["AppHost:Path"]
            ?? configuration[KnownConfigNames.DcpWorkloadId]
            ?? Path.GetFullPath(appHostDirectory);
        var identityHash = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        _stateDirectory = Path.Combine(appHostDirectory, ".aspire", "state");
        _statePath = Path.Combine(_stateDirectory, $"{identityHash}.json");
        _lockPath = _statePath + ".lock";
    }

    // Keep reporting the store as available after a disk failure so callers continue to reserve
    // ports in memory even though persistence has been disabled for the rest of this process.
    public bool IsAvailable => _statePath is not null;

    public T Read<T>(string key, Func<JsonNode?, T> readValue, T defaultValue)
    {
        if (_statePath is null || _lockPath is null || IsDiskAccessDisabled)
        {
            return defaultValue;
        }

        try
        {
            using var fileLock = FileLock.Acquire(_lockPath, _lockTimeout);
            var state = LoadState();
            return readValue(state[key]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            if (DisableDiskAccess())
            {
                _logger.LogWarning(
                    exception,
                    "Failed to read AppHost local state from {StatePath}. AppHost local state disk access is disabled for this process.",
                    _statePath);
            }

            return defaultValue;
        }
    }

    public void Update(string key, Func<JsonNode?, JsonNode?> updateValue)
    {
        if (_stateDirectory is null || _statePath is null || _lockPath is null || IsDiskAccessDisabled)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_stateDirectory);
            EnsureStateDirectoryIsIgnored();

            if (IsDiskAccessDisabled)
            {
                return;
            }

            using var fileLock = FileLock.Acquire(_lockPath, _lockTimeout);
            var state = LoadState();
            var currentValue = state[key];
            var updatedValue = updateValue(currentValue?.DeepClone());
            if (JsonNode.DeepEquals(currentValue, updatedValue))
            {
                return;
            }

            if (updatedValue is null)
            {
                state.Remove(key);
            }
            else
            {
                state[key] = updatedValue;
            }

            WriteState(state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            if (DisableDiskAccess())
            {
                _logger.LogWarning(
                    exception,
                    "Failed to update AppHost local state in {StatePath}. AppHost local state disk access is disabled for this process.",
                    _statePath);
            }
        }
    }

    private bool IsDiskAccessDisabled => Volatile.Read(ref _diskAccessDisabled) != 0;

    private bool DisableDiskAccess()
    {
        return Interlocked.Exchange(ref _diskAccessDisabled, 1) == 0;
    }

    private JsonObject LoadState()
    {
        if (!File.Exists(_statePath))
        {
            return [];
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(_statePath)) is not JsonObject root)
            {
                _logger.LogWarning("Ignoring invalid AppHost local state file {StatePath}.", _statePath);
                return [];
            }

            return root;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Ignoring malformed AppHost local state file {StatePath}.", _statePath);
            return [];
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception, "Ignoring invalid AppHost local state file {StatePath}.", _statePath);
            return [];
        }
    }

    private void WriteState(JsonObject state)
    {
        var statePath = _statePath
            ?? throw new InvalidOperationException("AppHost local state path was not initialized.");
        var temporaryPath = statePath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(temporaryPath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void EnsureStateDirectoryIsIgnored()
    {
        var ignorePath = Path.Combine(_stateDirectory!, ".gitignore");
        if (File.Exists(ignorePath))
        {
            return;
        }

        try
        {
            // State is derived from the AppHost model and must not require repository-specific ignore rules.
            using var stream = new FileStream(ignorePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine("*");
        }
        catch (IOException) when (File.Exists(ignorePath))
        {
            // Another AppHost process created the same product-owned ignore file.
        }
    }

}
