// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Aspire.Dashboard.Serialization;
using Microsoft.JSInterop;

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
/// A stored value that can't be read, for example because the circuit is disconnected or the value can't be
/// deserialized, is never overwritten. Changes stay in memory and are saved with a later change once the value can
/// be read.
/// </para>
/// <para>
/// Settings are stored unprotected. Protected values are encrypted with the dashboard's data protection key, which
/// changes between Aspire versions and on every run of the standalone dashboard, so protected settings would be lost
/// regularly. The settings stored here are names chosen by the user or reported by the app, not secrets.
/// </para>
/// <para>
/// Changes can happen on any thread, including resource subscription threads, so writes don't wait for the browser.
/// Writes run one at a time, each saving every change queued when it starts.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The stored value. It must be registered with the dashboard's JSON serializer context.</typeparam>
internal sealed class LocalStorageSetting<TValue> where TValue : class
{
    private readonly ILocalStorage _localStorage;
    private readonly ILogger _logger;
    private readonly Func<TValue?, TValue> _normalize;
    private readonly Action _onChanged;
    private readonly JsonTypeInfo<TValue> _typeInfo;
    private readonly Lock _lock = new();
    private readonly List<Func<TValue, bool>> _pendingChanges = [];
    private TValue _value;
    private Task? _loadTask;
    private bool _isWriting;

    /// <param name="localStorage">The browser's local storage.</param>
    /// <param name="key">The local storage key.</param>
    /// <param name="logger">Logs values that can't be saved.</param>
    /// <param name="normalize">
    /// Creates a value that changes can be applied to from a stored value, or from <see langword="null"/> when nothing
    /// is stored. Stored values can be edited in the browser, so this also drops invalid entries.
    /// </param>
    /// <param name="onChanged">Invoked, on any thread, after the in-memory value changes.</param>
    public LocalStorageSetting(ILocalStorage localStorage, string key, ILogger logger, Func<TValue?, TValue> normalize, Action onChanged)
    {
        _localStorage = localStorage;
        Key = key;
        _logger = logger;
        _normalize = normalize;
        _onChanged = onChanged;
        _typeInfo = (JsonTypeInfo<TValue>)DashboardJsonSerializerContext.Default.GetTypeInfo(typeof(TValue))!;
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
    /// Reads the stored value into memory, once per circuit, or again after a read failed.
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

            Adopt(result.Success ? result.Value : null, savedChangeCount: 0);
        }
        finally
        {
            loaded.SetResult();
        }

        // Changes made while the stored value couldn't be read are saved now that it can be.
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

            try
            {
                var result = await _localStorage.GetUnprotectedAsync<TValue>(Key).ConfigureAwait(false);
                if (result.ReadFailed)
                {
                    _logger.LogDebug("Unable to read '{Key}' from browser local storage. Changes are kept in memory and saved with the next change.", Key);
                    StopWriting();
                    return;
                }

                var value = _normalize(result.Success ? result.Value : null);
                foreach (var change in changes)
                {
                    change(value);
                }

                await _localStorage.SetUnprotectedAsync(Key, value).ConfigureAwait(false);
                Adopt(value, changes.Length);
            }
            catch (JSDisconnectedException)
            {
                // The browser is gone, so there's nowhere to save to. The next circuit reads what was saved before.
                StopWriting();
                return;
            }
            catch (Exception ex)
            {
                // Settings are a convenience: a change that can't be saved is still applied for the rest of the circuit,
                // and saved with the next change if possible.
                _logger.LogWarning(ex, "Unable to save '{Key}' to browser local storage.", Key);
                StopWriting();
                return;
            }
        }
    }

    private void StopWriting()
    {
        lock (_lock)
        {
            _isWriting = false;
        }
    }

    /// <summary>
    /// Makes a value read from storage, or just saved, the in-memory value, with changes that aren't saved yet applied.
    /// </summary>
    private void Adopt(TValue? stored, int savedChangeCount)
    {
        bool changed;
        lock (_lock)
        {
            // Only the writer removes changes, and they're always the oldest ones, so the first ones are those it saved.
            _pendingChanges.RemoveRange(0, savedChangeCount);
            var value = _normalize(stored);
            foreach (var change in _pendingChanges)
            {
                change(value);
            }

            // Another tab might not have changed anything. Comparing serialized values avoids notifying views, and
            // re-rendering them, after every save.
            changed = JsonSerializer.Serialize(value, _typeInfo) != JsonSerializer.Serialize(_value, _typeInfo);
            _value = value;
        }

        if (changed)
        {
            _onChanged();
        }
    }
}
