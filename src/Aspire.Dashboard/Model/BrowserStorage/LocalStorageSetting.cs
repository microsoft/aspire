// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.JSInterop;

namespace Aspire.Dashboard.Model.BrowserStorage;

/// <summary>
/// Keeps one application preference in memory for a circuit, publishing edits only after they are saved to browser storage.
/// </summary>
/// <remarks>
/// Each edit reads the latest stored value under a per-key lock to preserve edits saved by other tabs.
/// A failed read prevents writing a partial value. Failed saves are not retried.
/// If the stored value is invalid for the selected protection mode, the next successful edit replaces it.
/// </remarks>
internal sealed class LocalStorageSetting<TValue> where TValue : class
{
    private static readonly TimeSpan s_saveLockTimeout = TimeSpan.FromSeconds(10);

    private readonly ILocalStorage _localStorage;
    private readonly LocalStorageLocks _locks;
    private readonly ILogger _logger;
    private readonly bool _protect;
    private readonly Func<TValue?, TValue> _normalize;
    private readonly Func<TValue, TValue, bool> _equals;
    private readonly Action _onChanged;
    private readonly Lock _lock = new();
    private TValue _value;
    private Task? _loadTask;
    private long _storageVersion;

    public LocalStorageSetting(ILocalStorage localStorage, LocalStorageLocks locks, string key, ILogger logger, bool protect, Func<TValue?, TValue> normalize, Func<TValue, TValue, bool> equals, Action onChanged)
    {
        _localStorage = localStorage;
        _locks = locks;
        Key = key;
        _logger = logger;
        _protect = protect;
        _normalize = normalize;
        _equals = equals;
        _onChanged = onChanged;
        _value = normalize(null);
    }

    public string Key { get; }

    /// <summary>
    /// Reads the in-memory value. <paramref name="read"/> must not keep a reference to the value or change it.
    /// </summary>
    public TResult Read<TResult>(Func<TValue, TResult> read)
    {
        lock (_lock)
        {
            return read(_value);
        }
    }

    public Task EnsureLoadedAsync()
    {
        TaskCompletionSource? loaded = null;
        Task loadTask;
        lock (_lock)
        {
            if (_loadTask is null)
            {
                loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _loadTask = loaded.Task;
            }
            loadTask = _loadTask;
        }

        // Even when the browser answers synchronously, notify listeners outside the state lock.
        if (loaded is not null)
        {
            _ = LoadAsync(loaded);
        }

        return loadTask;
    }

    // The change operates on a fresh copy. An Unchanged result must leave that copy unmodified.
    public async Task<SettingUpdateResult> UpdateAsync(Func<TValue, SettingUpdateResult> change)
    {
        // Hold the lock across both browser operations so another tab can't write between the read and write.
        var saveLock = _locks.GetLock(Key);
        if (!await saveLock.WaitAsync(s_saveLockTimeout).ConfigureAwait(false))
        {
            _logger.LogWarning("Unable to save '{Key}' because the browser storage lock timed out.", Key);
            return SettingUpdateResult.NotSaved;
        }

        bool changed;
        SettingUpdateResult result;
        try
        {
            var stored = await ReadStoredAsync().ConfigureAwait(false);
            if (stored.ReadFailed)
            {
                _logger.LogWarning("Unable to save '{Key}' because browser local storage could not be read.", Key);
                return SettingUpdateResult.NotSaved;
            }

            var value = _normalize(stored.Success ? stored.Value : null);
            result = change(value);
            if (result == SettingUpdateResult.Changed)
            {
                await (_protect ? _localStorage.SetAsync(Key, value) : _localStorage.SetUnprotectedAsync(Key, value)).ConfigureAwait(false);
            }
            else if (result != SettingUpdateResult.Unchanged || !stored.Success)
            {
                return result;
            }

            // A no-op can refresh a stale tab with the confirmed value that another tab already saved.
            changed = Adopt(value, loadVersion: null);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to save '{Key}' to browser local storage.", Key);
            return SettingUpdateResult.NotSaved;
        }
        finally
        {
            saveLock.Release();
        }

        if (changed)
        {
            _onChanged();
        }

        return result;
    }

    private async Task LoadAsync(TaskCompletionSource loaded)
    {
        try
        {
            long loadVersion;
            lock (_lock)
            {
                loadVersion = _storageVersion;
            }

            var result = await ReadStoredAsync().ConfigureAwait(false);
            if (result.ReadFailed)
            {
                // Load again when another view asks, for example after the circuit reconnects.
                lock (_lock)
                {
                    _loadTask = null;
                }
                return;
            }
            if (result.InvalidValue)
            {
                _logger.LogWarning("'{Key}' in browser local storage can't be read. It's replaced with the next change.", Key);
            }

            if (Adopt(_normalize(result.Success ? result.Value : null), loadVersion))
            {
                _onChanged();
            }
        }
        catch (Exception ex)
        {
            loaded.SetException(ex);
            return;
        }

        // Read failures also complete the load attempt so the view can render.
        finally
        {
            loaded.TrySetResult();
        }
    }

    private Task<StorageResult<TValue>> ReadStoredAsync() => _protect
        ? _localStorage.GetAsync<TValue>(Key)
        : _localStorage.GetUnprotectedAsync<TValue>(Key);

    private bool Adopt(TValue value, long? loadVersion)
    {
        lock (_lock)
        {
            if (loadVersion is { } version && version != _storageVersion)
            {
                // An older load must not overwrite a value confirmed while it was waiting on storage.
                return false;
            }
            if (loadVersion is null)
            {
                _storageVersion++;
            }

            var changed = !_equals(_value, value);
            _value = value;
            return changed;
        }
    }
}

internal enum SettingUpdateResult
{
    Unchanged,
    Changed,
    LimitReached,
    NotSaved
}
