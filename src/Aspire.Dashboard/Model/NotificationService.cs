// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Thread-safe singleton implementation of <see cref="INotificationService"/>.
/// </summary>
internal sealed class NotificationService(TimeProvider timeProvider) : INotificationService
{
    private const int MaxNotifications = 100;

    private readonly object _lock = new();
    private readonly List<(string Id, NotificationEntry Entry)> _notifications = [];
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

    public IReadOnlyList<NotificationMessage> GetNotifications()
    {
        lock (_lock)
        {
            // Return a snapshot, most recent first.
            var result = new NotificationMessage[_notifications.Count];
            for (var i = 0; i < _notifications.Count; i++)
            {
                var item = _notifications[_notifications.Count - 1 - i];
                result[i] = new NotificationMessage { Id = item.Id, Entry = item.Entry };
            }

            return result;
        }
    }

    public string AddNotification(NotificationEntry notification)
    {
        notification.Timestamp = timeProvider.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            _notifications.Add((id, notification));
            _unreadCount++;

            // Remove oldest notifications when the limit is exceeded.
            while (_notifications.Count > MaxNotifications)
            {
                _notifications.RemoveAt(0);
            }
        }

        OnChange?.Invoke();
        return id;
    }

    public void ReplaceNotification(string id, NotificationEntry notification)
    {
        notification.Timestamp = timeProvider.GetUtcNow();
        lock (_lock)
        {
            for (var i = 0; i < _notifications.Count; i++)
            {
                if (_notifications[i].Id == id)
                {
                    _notifications[i] = (id, notification);
                    break;
                }
            }
        }

        OnChange?.Invoke();
    }

    public void RemoveNotification(string id)
    {
        lock (_lock)
        {
            _notifications.RemoveAll(n => n.Id == id);
        }

        OnChange?.Invoke();
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _notifications.Clear();
            _unreadCount = 0;
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
