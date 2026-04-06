// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Tracks the unread notification count for the dashboard notification center.
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
    /// Increments the unread count by one and raises <see cref="OnChange"/>.
    /// </summary>
    void IncrementUnreadCount();

    /// <summary>
    /// Resets the unread count to zero and raises <see cref="OnChange"/>.
    /// </summary>
    void ResetUnreadCount();

    /// <summary>
    /// Raised when the unread count changes.
    /// </summary>
    event Action? OnChange;
}
