// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

/// <summary>
/// Implementation of IMessageService that tracks displayed messages.
/// In v5, message bars are managed via component state rather than a centralized service,
/// so this implementation raises events that UI components can subscribe to.
/// </summary>
internal sealed class MessageService : IMessageService
{
    private readonly List<MessageEntry> _messages = new();
    private readonly object _lock = new();

    public event Action? OnMessageChanged;

    public IReadOnlyList<MessageEntry> Messages
    {
        get
        {
            lock (_lock)
            {
                return _messages.ToList();
            }
        }
    }

    public Task<Message> ShowMessageBarAsync(Action<MessageOptions> configure)
    {
        var options = new MessageOptions();
        configure(options);

        var message = new Message(() => RemoveMessage(options));

        lock (_lock)
        {
            _messages.Add(new MessageEntry(options, message));
        }

        OnMessageChanged?.Invoke();

        return Task.FromResult(message);
    }

    private void RemoveMessage(MessageOptions options)
    {
        lock (_lock)
        {
            _messages.RemoveAll(m => ReferenceEquals(m.Options, options));
        }

        OnMessageChanged?.Invoke();
    }
}

/// <summary>
/// Represents an active message entry with its options and handle.
/// </summary>
internal sealed record MessageEntry(MessageOptions Options, Message Message);
