// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Dashboard.Model.BrowserStorage;

/// <summary>
/// Makes saves of a setting in the browser's local storage run one at a time across circuits.
/// </summary>
/// <remarks>
/// Each tab of the dashboard has its own circuit, and the tabs of a browser share its local storage. A save reads the
/// stored value, applies changes to it and writes it back, so two tabs saving at the same time could both read the
/// same value, and the second write would replace the first tab's change. Every circuit runs in this dashboard
/// process, so holding a lock for the storage key during the whole save makes the saves of a setting run one after
/// another. Saves from different browsers, which don't share storage, also wait for each other: saves are rare and
/// short, so telling browsers apart isn't worth it.
/// </remarks>
public sealed class LocalStorageLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    // Keys are a fixed set of settings per application name, so the locks aren't removed.
    internal SemaphoreSlim GetLock(string key) => _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
}
