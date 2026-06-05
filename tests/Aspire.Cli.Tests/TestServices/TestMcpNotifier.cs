// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Mcp;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A test implementation of <see cref="IMcpNotifier"/> that collects notifications.
/// </summary>
internal sealed class TestMcpNotifier : IMcpNotifier
{
    private readonly List<string> _notifications = [];

    /// <summary>
    /// Gets the list of notification methods that have been sent.
    /// </summary>
    public IReadOnlyList<string> Notifications => _notifications;

    /// <inheritdoc />
    public Task SendNotificationAsync(string method, CancellationToken cancellationToken = default)
    {
        _notifications.Add(method);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendNotificationAsync<TParams>(string method, TParams parameters, CancellationToken cancellationToken = default)
    {
        _notifications.Add(method);
        return Task.CompletedTask;
    }
}
