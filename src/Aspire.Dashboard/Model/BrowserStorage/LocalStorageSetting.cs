// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.BrowserStorage;

/// <summary>
/// Keeps one setting that describes the application, such as resource tags, in memory for a circuit and saves it to
/// the browser's local storage.
/// </summary>
/// <remarks>
/// <para>
/// Every tab of the dashboard has its own circuit, and so its own copy of the setting, but they share local storage.
/// Writing a tab's copy would replace changes another tab saved since this tab read the setting. Instead, each change
/// is a function that's applied to the in-memory value right away and queued. Writes read the latest stored value,
/// apply the queued changes to it and save the result, which then becomes the in-memory value, so a tab also picks up
/// what other tabs saved. Changes must be idempotent, such as "tag api with backend", because they're applied again
/// to every value read before they're saved.
/// </para>
/// <para>
/// When storage can't be read, for example while the circuit is disconnected, nothing is written: writing then would
/// replace the stored value with one missing everything it contained. Writes are retried a few times with increasing
/// delays, and again with the next change or when a view loads the setting. A stored value that can't be
/// deserialized, for example because it was saved by another version of the dashboard, can't become readable, so it's
/// copied to <c>{key}_Unreadable</c>, unless a value was already copied there, and then replaced, rather than
/// preventing the setting from being saved again.
/// </para>
/// <para>
/// Settings are stored unprotected. Protected values are encrypted with the dashboard's data protection key, which
/// changes between Aspire versions and on every run of the standalone dashboard, so protected settings would be lost
/// regularly. The settings stored here are names chosen by the user or reported by the app, not secrets.
/// </para>
/// <para>
/// Changes can happen on any thread, including resource subscription threads, so writes don't wait for the browser.
/// Writes run one at a time, each saving every change queued when it starts, and saves of the same setting from
/// different tabs run one at a time too; see <see cref="LocalStorageLocks"/>.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The stored value. It must be registered with the dashboard's JSON serializer context.</typeparam>
internal sealed class LocalStorageSetting<TValue> where TValue : class
{
    // Retries cover a reconnecting circuit: delays of 1, 2, 4 and 8 seconds wait about 15 seconds in all.
    private const int MaxWriteAttempts = 5;
    private static readonly TimeSpan s_firstRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_saveLockTimeout = TimeSpan.FromSeconds(10);

    private readonly ILocalStorage _localStorage;
    private readonly LocalStorageLocks _locks;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Func<TValue?, TValue> _normalize;
    private readonly Func<TValue, TValue, bool> _equals;
    private readonly Action _onChanged;
    private readonly Lock _lock = new();
    private readonly List<Func<TValue, bool>> _pendingChanges = [];
    private TValue _value;
    private Task? _loadTask;
    private bool _isWriting;

    // Incremented after each save. A load that read storage before a save completed has an older value than the one
    // the save adopted, so it must not replace it.
    private long _saveCount;

    /// <param name="localStorage">The browser's local storage.</param>
    /// <param name="locks">Makes saves of the setting from different tabs run one at a time.</param>
    /// <param name="timeProvider">Times the delays between save retries.</param>
    /// <param name="key">The local storage key.</param>
    /// <param name="logger">Logs values that can't be read or saved.</param>
    /// <param name="normalize">
    /// Creates a value that changes can be applied to from a stored value, or from <see langword="null"/> when nothing
    /// is stored. Stored values can be edited in the browser, so this also drops invalid entries.
    /// </param>
    /// <param name="equals">Returns whether two normalized values have the same content, to only notify real changes.</param>
    /// <param name="onChanged">Invoked, on any thread, after the in-memory value changes.</param>
    public LocalStorageSetting(ILocalStorage localStorage, LocalStorageLocks locks, TimeProvider timeProvider, string key, ILogger logger, Func<TValue?, TValue> normalize, Func<TValue, TValue, bool> equals, Action onChanged)
    {
        _localStorage = localStorage;
        _locks = locks;
        _timeProvider = timeProvider;
        Key = key;
        _logger = logger;
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

    /// <summary>
    /// Reads the stored value into memory, once per circuit, or again after storage couldn't be read. Also saves
    /// changes that earlier writes couldn't save.
    /// </summary>
    public Task EnsureLoadedAsync()
    {
        TaskCompletionSource? loaded = null;
        Task loadTask;
        lock (_lock)
        {
            if (_loadTask is null)
            {
                // Continuations can run inline: the task is completed outside the lock, and when storage answers
                // synchronously the task is already complete when it's returned, so callers don't yield.
                loaded = new TaskCompletionSource();
                _loadTask = loaded.Task;
            }
            loadTask = _loadTask;
        }

        // Started outside the lock: with storage that completes synchronously, loading would otherwise raise
        // change notifications while holding it.
        if (loaded is not null)
        {
            _ = LoadAsync(loaded);
        }
        else if (loadTask.IsCompleted)
        {
            StartWritingIfPending();
        }

        return loadTask;
    }

    /// <summary>
    /// Applies a change to the in-memory value and saves it in the background.
    /// </summary>
    /// <param name="change">Changes a value and returns whether it changed. It must be idempotent.</param>
    /// <returns><see langword="true"/> when the in-memory value changed.</returns>
    public bool Update(Func<TValue, bool> change)
    {
        bool startWriting;
        lock (_lock)
        {
            if (!change(_value))
            {
                return false;
            }

            _pendingChanges.Add(change);
            startWriting = !_isWriting;
            _isWriting = true;
        }

        _onChanged();
        if (startWriting)
        {
            _ = WriteAsync();
        }
        return true;
    }

    private async Task LoadAsync(TaskCompletionSource loaded)
    {
        try
        {
            long saveCount;
            lock (_lock)
            {
                saveCount = _saveCount;
            }

            var result = await _localStorage.GetUnprotectedAsync<TValue>(Key).ConfigureAwait(false);
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
                _logger.LogWarning("'{Key}' in browser local storage can't be read by this version of the dashboard. It's replaced with the next change and kept as '{Key}_Unreadable'.", Key, Key);
            }

            Adopt(result.Success ? result.Value : null, savedChangeCount: 0, loadedAfterSaveCount: saveCount);
        }
        catch (Exception ex)
        {
            // The setting stays as it is in memory. Not retried: an error here comes from the stored value, which
            // would fail the same way again.
            _logger.LogWarning(ex, "Unable to load '{Key}' from browser local storage.", Key);
        }
        finally
        {
            loaded.SetResult();
        }

        // Changes made while the stored value couldn't be read are saved now that it can be.
        StartWritingIfPending();
    }

    private void StartWritingIfPending()
    {
        bool startWriting;
        lock (_lock)
        {
            startWriting = _pendingChanges.Count > 0 && !_isWriting;
            _isWriting |= startWriting;
        }
        if (startWriting)
        {
            _ = WriteAsync();
        }
    }

    private async Task WriteAsync()
    {
        var failedAttempts = 0;
        while (true)
        {
            Func<TValue, bool>[] changes;
            lock (_lock)
            {
                if (_pendingChanges.Count == 0)
                {
                    _isWriting = false;
                    return;
                }
                changes = [.. _pendingChanges];
            }

            Exception? error = null;
            try
            {
                if (await TryWriteAsync(changes).ConfigureAwait(false))
                {
                    failedAttempts = 0;
                    continue;
                }
            }
            catch (Exception ex)
            {
                // Includes a disconnected circuit, which can reconnect.
                error = ex;
            }

            failedAttempts++;
            if (failedAttempts == MaxWriteAttempts)
            {
                // Settings are a convenience: changes stay applied for the rest of the circuit, and are saved with the
                // next change or when a view loads the setting, if storage can be used again by then.
                _logger.LogWarning(error, "Unable to save '{Key}' to browser local storage.", Key);
                lock (_lock)
                {
                    _isWriting = false;
                }
                return;
            }

            await Task.Delay(s_firstRetryDelay * Math.Pow(2, failedAttempts - 1), _timeProvider).ConfigureAwait(false);
        }
    }

    /// <returns><see langword="false"/> when storage couldn't be read, so nothing was written.</returns>
    private async Task<bool> TryWriteAsync(Func<TValue, bool>[] changes)
    {
        // Another tab saving the same setting between this read and write would have its change replaced, so the whole
        // save holds the setting's lock. A browser that stops answering would otherwise hold up saves in other tabs
        // until its JavaScript call times out; giving up is retried like storage that can't be read.
        var saveLock = _locks.GetLock(Key);
        if (!await saveLock.WaitAsync(s_saveLockTimeout).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            return await TryWriteLockedAsync(changes).ConfigureAwait(false);
        }
        finally
        {
            saveLock.Release();
        }
    }

    private async Task<bool> TryWriteLockedAsync(Func<TValue, bool>[] changes)
    {
        var result = await _localStorage.GetUnprotectedAsync<TValue>(Key).ConfigureAwait(false);
        if (result.ReadFailed)
        {
            return false;
        }
        if (result.InvalidValue)
        {
            // Keep the value for the version of the dashboard that saved it, or for someone to recover it by hand.
            // Only the first unreadable
            // value is kept: dashboard versions that alternate on the same app would otherwise keep replacing each
            // other's backup, until nothing of the first value is left.
            var backupKey = $"{Key}_Unreadable";
            var backup = await _localStorage.GetRawUnprotectedAsync(backupKey).ConfigureAwait(false);
            if (backup.ReadFailed)
            {
                return false;
            }
            if (!backup.Success)
            {
                // Copied as text, so a value that isn't valid JSON, such as one cut short, is kept too.
                var unreadable = await _localStorage.GetRawUnprotectedAsync(Key).ConfigureAwait(false);
                if (unreadable.ReadFailed)
                {
                    return false;
                }
                if (unreadable.Success)
                {
                    await _localStorage.SetRawUnprotectedAsync(backupKey, unreadable.Value).ConfigureAwait(false);
                }
            }
        }

        var value = _normalize(result.Success ? result.Value : null);
        foreach (var change in changes)
        {
            change(value);
        }

        await _localStorage.SetUnprotectedAsync(Key, value).ConfigureAwait(false);
        Adopt(value, changes.Length, loadedAfterSaveCount: null);
        return true;
    }

    /// <summary>
    /// Makes a value read from storage, or just saved, the in-memory value, with changes that aren't saved yet applied.
    /// </summary>
    /// <param name="stored">The stored value, or <see langword="null"/> when nothing is stored.</param>
    /// <param name="savedChangeCount">The number of queued changes that <paramref name="stored"/> includes.</param>
    /// <param name="loadedAfterSaveCount">For a load, the number of saves completed when it read storage.</param>
    private void Adopt(TValue? stored, int savedChangeCount, long? loadedAfterSaveCount)
    {
        bool changed;
        lock (_lock)
        {
            if (loadedAfterSaveCount is { } count && count != _saveCount)
            {
                // A save completed after this load read storage, and adopted a value read later.
                return;
            }
            if (loadedAfterSaveCount is null)
            {
                _saveCount++;
            }

            // Only the writer removes changes, and they're always the oldest ones, so the first ones are those it saved.
            _pendingChanges.RemoveRange(0, savedChangeCount);
            var value = _normalize(stored);
            foreach (var change in _pendingChanges)
            {
                change(value);
            }

            changed = !_equals(_value, value);
            _value = value;
        }

        if (changed)
        {
            _onChanged();
        }
    }
}
