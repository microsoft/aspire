// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

public interface IDashboardMessageService
{
    event Action? OnChange;

    IReadOnlyList<DashboardMessage> GetMessages();

    DashboardMessage Show(DashboardMessageOptions options);
}

public sealed class DashboardMessageOptions
{
    public required string Title { get; init; }

    public required string Body { get; init; }

    public required NotificationIntent Intent { get; init; }

    public bool AllowDismiss { get; init; }

    public string? LinkText { get; init; }

    public string? LinkUrl { get; init; }

    public Func<Task>? OnClose { get; init; }
}

public sealed class DashboardMessage
{
    private readonly Func<Task> _closeAsync;

    internal DashboardMessage(string id, DashboardMessageOptions options, Func<Task> closeAsync)
    {
        Id = id;
        Options = options;
        _closeAsync = closeAsync;
    }

    public string Id { get; }

    public DashboardMessageOptions Options { get; }

    public void Close()
    {
        _ = _closeAsync();
    }

    public Task CloseAsync() => _closeAsync();
}

internal sealed class DashboardMessageService : IDashboardMessageService
{
    private readonly object _lock = new();
    private readonly List<DashboardMessage> _messages = [];

    public event Action? OnChange;

    public IReadOnlyList<DashboardMessage> GetMessages()
    {
        lock (_lock)
        {
            return _messages.ToArray();
        }
    }

    public DashboardMessage Show(DashboardMessageOptions options)
    {
        var id = Guid.NewGuid().ToString("N");
        var message = new DashboardMessage(id, options, () => CloseAsync(id));
        lock (_lock)
        {
            _messages.Insert(0, message);
        }

        OnChange?.Invoke();
        return message;
    }

    private async Task CloseAsync(string id)
    {
        DashboardMessage? removedMessage = null;
        lock (_lock)
        {
            var index = _messages.FindIndex(m => m.Id == id);
            if (index >= 0)
            {
                removedMessage = _messages[index];
                _messages.RemoveAt(index);
            }
        }

        if (removedMessage is not null)
        {
            OnChange?.Invoke();
            if (removedMessage.Options.OnClose is { } onClose)
            {
                await onClose().ConfigureAwait(false);
            }
        }
    }
}
