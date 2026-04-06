// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Thread-safe singleton implementation of <see cref="INotificationService"/>.
/// </summary>
internal sealed class NotificationService : INotificationService
{
    private readonly object _lock = new();
    private int _unreadCount;

    public int UnreadCount
    {
        get
        {
            lock (_lock)
            {
                return _unreadCount;
            }
        }
    }

    public event Action? OnChange;

    public void IncrementUnreadCount()
    {
        lock (_lock)
        {
            _unreadCount++;
        }

        OnChange?.Invoke();
    }

    public void ResetUnreadCount()
    {
        lock (_lock)
        {
            _unreadCount = 0;
        }

        OnChange?.Invoke();
    }
}
