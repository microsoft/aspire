// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.JSInterop;

namespace Aspire.Dashboard.Model.BrowserStorage;

/// <summary>
/// Reads and writes one setting in the browser's local storage, for settings stores that keep their state in memory
/// and save a snapshot of it after each change.
/// </summary>
/// <remarks>
/// <para>
/// Settings are stored unprotected. Protected values are encrypted with the dashboard's data protection key, which
/// changes between Aspire versions and on every run of the standalone dashboard, so protected settings would be lost
/// regularly. The settings stored here are names chosen by the user or reported by the app, not secrets.
/// </para>
/// <para>
/// Changes can happen on any thread, including resource subscription threads, so writes don't wait for the browser.
/// Writes run one at a time, and a write that's queued while another is in progress is replaced by the next one, so
/// the browser always ends up with the latest snapshot even when changes happen faster than the browser saves them.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The stored value. It must be registered with the dashboard's JSON serializer context.</typeparam>
internal sealed class LocalStorageSetting<TValue> where TValue : class
{
    private readonly ILocalStorage _localStorage;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private TValue? _pendingValue;
    private bool _isWriting;

    public LocalStorageSetting(ILocalStorage localStorage, string key, ILogger logger)
    {
        _localStorage = localStorage;
        Key = key;
        _logger = logger;
    }

    public string Key { get; }

    /// <summary>
    /// Reads the stored value, or <see langword="null"/> when nothing is stored or the stored value can't be read.
    /// </summary>
    public async Task<TValue?> ReadAsync()
    {
        // Read errors, including a disconnected circuit, are logged by the storage and reported as a missing value.
        var result = await _localStorage.GetUnprotectedAsync<TValue>(Key).ConfigureAwait(false);
        return result.Success ? result.Value : null;
    }

    /// <summary>
    /// Saves a snapshot in the background. The snapshot must not be changed after it's passed in.
    /// </summary>
    public void Write(TValue value)
    {
        lock (_lock)
        {
            _pendingValue = value;
            if (_isWriting)
            {
                return;
            }
            _isWriting = true;
        }

        _ = WriteQueuedValuesAsync();
    }

    private async Task WriteQueuedValuesAsync()
    {
        while (true)
        {
            TValue value;
            lock (_lock)
            {
                if (_pendingValue is null)
                {
                    _isWriting = false;
                    return;
                }
                value = _pendingValue;
                _pendingValue = null;
            }

            try
            {
                await _localStorage.SetUnprotectedAsync(Key, value).ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The browser is gone, so there's nowhere to save to. The next circuit reads what was saved before.
            }
            catch (Exception ex)
            {
                // Settings are a convenience: a change that can't be saved is still applied for the rest of the circuit.
                _logger.LogWarning(ex, "Unable to save '{Key}' to browser local storage.", Key);
            }
        }
    }
}
