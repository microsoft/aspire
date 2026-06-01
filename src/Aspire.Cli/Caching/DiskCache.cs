// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Caching;

internal sealed class DiskCache : IDiskCache
{
    private static readonly TimeSpan s_defaultCacheExpiryWindow = TimeSpan.FromHours(3);
    private static readonly TimeSpan s_defaultMaxCacheAge = TimeSpan.FromDays(7);

    private readonly ILogger<DiskCache> _logger;
    private readonly DirectoryInfo _cacheDirectory;
    private readonly TimeSpan _expiryWindow;
    private readonly TimeSpan _maxAge;

    public DiskCache(ILogger<DiskCache> logger, CliExecutionContext executionContext, IConfiguration configuration)
    {
        _logger = logger;
        _cacheDirectory = new DirectoryInfo(Path.Combine(executionContext.CacheDirectory.FullName, "nuget-search"));
        if (!_cacheDirectory.Exists)
        {
            try
            {
                _cacheDirectory.Create();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to create cache directory {CacheDirectory}", _cacheDirectory.FullName);
            }
        }
        _expiryWindow = ReadWindow(configuration, "PackageSearchCacheExpirySeconds", s_defaultCacheExpiryWindow);
        _maxAge = ReadWindow(configuration, "PackageSearchMaxCacheAgeSeconds", s_defaultMaxCacheAge);
    }

    private static TimeSpan ReadWindow(IConfiguration configuration, string key, TimeSpan fallback)
    {
        if (configuration[key] is string secondsString && double.TryParse(secondsString, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }
        return fallback;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            // The key could be any string which is not necessary suitable as a string so we convert
            // it to a hex encoded SHA256 hash of the string value.
            var keyHash = HashKey(key);

            // Once we hashed key we resolve the path to the cache file. The fully qualified cache
            // file will include a timestamp. As part of this call clean up of old cache entries may
            // occur to keep the cache directory clean. This may return null if there is a cache miss.
            var cacheFilePath = ResolveValidCacheFile(keyHash);
            if (cacheFilePath is null)
            {
                _logger.LogTrace("Disk cache miss for key {RawKey}", key);
                return null;
            }

            // Assuming here is a hit we attempt to read the file and return the string.
            _logger.LogTrace("Disk cache hit for key {RawKey} (file: {CacheFilePath})", key, cacheFilePath);
            return await File.ReadAllTextAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to retrieve or read cache entry for key {RawKey}", key);
            return null;
        }
    }

    public async Task SetAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        try
        {
            // If the cache directory doesn't exist create it, but if it blows up just return
            // failing to write the cache entry shouldn't be a fatal operation. If people notice
            // that things aren't going fast enough tey can run in debug mode and they'll see the
            // messages appearing  there.
            if (!_cacheDirectory.Exists)
            {
                try
                {
                    _cacheDirectory.Create();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to create cache directory {CacheDirectory}", _cacheDirectory.FullName);
                    return;
                }
            }

            // Compute the hash of the key and generate the filename based on the
            // current using timestamp. To ensure we get a clean write we create a
            // temp file to write into and then copy the file over in one step. Once
            // again - if anything blows up we just move on.
            var keyHash = HashKey(key);
            var currentUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fileName = $"{keyHash}.{currentUnixTime}.json";
            var fullPath = Path.Combine(_cacheDirectory.FullName, fileName);
            var tempFile = fullPath + ".tmp";
            await File.WriteAllTextAsync(tempFile, content, cancellationToken).ConfigureAwait(false);

            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                }
                catch
                {
                    // Best effort; swallow per original intent.
                }
            }

            File.Move(tempFile, fullPath);
            _logger.LogTrace("Stored disk cache entry for key {RawKey} (file: {CacheFilePath})", key, fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write disk cache entry for key {RawKey}", key);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_cacheDirectory.Exists)
            {
                foreach (var file in _cacheDirectory.GetFiles("*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to delete cache file {CacheFile}", file.FullName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clear disk cache at {CacheDirectory}", _cacheDirectory.FullName);
        }
        return Task.CompletedTask;
    }

    private static string HashKey(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private string? ResolveValidCacheFile(string keyHash)
    {
        // The purpose of this method is to find the best cache hit (if one exists)
        // from the files that exist in the $HOME/.aspire/cache/nuget-cache directory.
        // The filename structure for cache files is <keyhash>.<unixtimestamp>.json and
        // each json file represents the JSOn payload from a single invocation from
        // an dotnet package search.

        // This method can absolutely be further optimized but its a huge leap up from
        // spawning a process that makes a network call :) Basically it looks at all the
        // files in the cache and then evicts the ones that are over the max age, and for
        // all the ones that match the key, it deletes the oldest ones keeping the youngest
        // assuming it is within the expiry window.

        try
        {
            if (!_cacheDirectory.Exists)
            {
                return null;
            }

            var currentUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var matchingFiles = _cacheDirectory.GetFiles($"{keyHash}.*.json");
            var allFiles = _cacheDirectory.GetFiles("*.json");
            var list = new List<(FileInfo file, long timestamp)>();

            foreach (var file in matchingFiles)
            {
                var nameNoExt = Path.GetFileNameWithoutExtension(file.Name);
                var parts = nameNoExt.Split('.');
                if (parts.Length >= 2 && long.TryParse(parts[1], out var ts))
                {
                    list.Add((file, ts));
                }
                else
                {
                    TryDelete(file, invalid: true);
                }
            }

            list.Sort((a, b) => b.timestamp.CompareTo(a.timestamp));

            string? valid = null;
            for (var i = 0; i < list.Count; i++)
            {
                var (file, ts) = list[i];
                var age = TimeSpan.FromSeconds(currentUnixTime - ts);

                if (i == 0)
                {
                    if (age <= _expiryWindow)
                    {
                        valid = file.FullName;
                    }
                    else
                    {
                        // first file exists but is expired
                        TryDelete(file, expired: true);
                    }
                }
                else
                {
                    // not the newest file; delete if it's expired (or unconditionally remove as older variants)
                    var isExpired = age > _expiryWindow;
                    TryDelete(file, expired: isExpired);
                }
            }

            foreach (var file in allFiles)
            {
                // Skip files already considered for this key
                if (matchingFiles.Contains(file))
                {
                    continue;
                }

                var nameNoExt = Path.GetFileNameWithoutExtension(file.Name);
                var parts = nameNoExt.Split('.');

                if (parts.Length >= 2 && long.TryParse(parts[1], out var ts))
                {
                    var age = TimeSpan.FromSeconds(currentUnixTime - ts);
                    if (age > _maxAge)
                    {
                        TryDelete(file, old: true);
                    }
                }
                else
                {
                    TryDelete(file, invalid: true);
                }
            }

            return valid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve cache file for key hash {KeyHash}", keyHash);
            return null;
        }
    }

    private void TryDelete(FileInfo file, bool expired = false, bool old = false, bool invalid = false)
    {
        try
        {
            file.Delete();
            if (expired)
            {
                _logger.LogTrace("Deleted expired cache file: {CacheFile}", file.FullName);
            }
            else if (old)
            {
                _logger.LogTrace("Deleted old cache file during global cleanup: {CacheFile}", file.FullName);
            }
            else if (invalid)
            {
                _logger.LogTrace("Deleted invalid cache file: {CacheFile}", file.FullName);
            }
            else
            {
                _logger.LogTrace("Deleted cache file: {CacheFile}", file.FullName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete cache file {CacheFile}", file.FullName);
        }
    }
}
