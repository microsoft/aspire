// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Stores notifications for the dashboard notification center.
/// Implementations must be thread-safe as the service is registered as a singleton
/// and accessed from multiple Blazor circuits.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Gets the number of notifications added since the dialog was last opened.
    /// </summary>
    int UnreadCount { get; }

    /// <summary>
    /// Gets a snapshot of the current notifications, most recent first.
    /// </summary>
    IReadOnlyList<NotificationMessage> GetNotifications();

    /// <summary>
    /// Adds a notification and raises <see cref="OnChange"/>.
    /// </summary>
    /// <returns>The ID of the added notification, which can be used to replace it later.</returns>
    string AddNotification(NotificationEntry notification);

    /// <summary>
    /// Replaces an existing notification (matched by <paramref name="id"/>) and raises <see cref="OnChange"/>.
    /// </summary>
    void ReplaceNotification(string id, NotificationEntry notification);

    /// <summary>
    /// Removes a notification by ID and raises <see cref="OnChange"/>.
    /// </summary>
    void RemoveNotification(string id);

    /// <summary>
    /// Removes all notifications and raises <see cref="OnChange"/>.
    /// </summary>
    void ClearAll();

    /// <summary>
    /// Resets the unread count to zero and raises <see cref="OnChange"/>.
    /// </summary>
    void ResetUnreadCount();

    /// <summary>
    /// Raised when notifications or the unread count change.
    /// </summary>
    event Action? OnChange;
}

/// <summary>
/// Represents a single notification in the notification center.
/// </summary>
public sealed class NotificationEntry
{
    public required string Title { get; init; }
    public string? Body { get; init; }
    public required MessageIntent Intent { get; init; }
    public DateTimeOffset Timestamp { get; set; }
    public NotificationAction? PrimaryAction { get; init; }
}

/// <summary>
/// An action button displayed on a notification.
/// </summary>
public sealed class NotificationAction
{
    public required string Text { get; init; }
    public required Func<IServiceProvider, Task> OnClick { get; init; }
}

/// <summary>
/// A notification with its service-assigned ID.
/// </summary>
public sealed class NotificationMessage
{
    public required string Id { get; init; }
    public required NotificationEntry Entry { get; init; }
}
