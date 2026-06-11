// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Tests.Shared;

public class TestMessageService : IMessageService
{
    private readonly Func<MessageOptions, Task<Message>>? _onShowMessage;

    public TestMessageService(Func<MessageOptions, Task<Message>>? onShowMessage = null)
    {
        _onShowMessage = onShowMessage;
    }

    public IEnumerable<Message> AllMessages { get; } = Enumerable.Empty<Message>();

#pragma warning disable CS0067
    public event Action? OnMessageItemsUpdated;
    public event Func<Task>? OnMessageItemsUpdatedAsync;
#pragma warning restore CS0067

    public void Clear(string? section = null)
    {
        throw new NotImplementedException();
    }

    public int Count(string? section)
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public IEnumerable<Message> MessagesToShow(int count = 5, string? section = null)
    {
        throw new NotImplementedException();
    }

    public void Remove(Message message)
    {
        throw new NotImplementedException();
    }

    public Message ShowMessageBar(Action<MessageOptions> options)
    {
        throw new NotImplementedException();
    }

    public Message ShowMessageBar(string title)
    {
        throw new NotImplementedException();
    }

    public Message ShowMessageBar(string title, MessageIntent intent)
    {
        throw new NotImplementedException();
    }

    public Message ShowMessageBar(string title, MessageIntent intent, string section)
    {
        throw new NotImplementedException();
    }

    public Message ShowMessageBar(MarkupString title, MessageIntent intent, string section)
    {
        throw new NotImplementedException();
    }

    public Task<Message> ShowMessageBarAsync(Action<MessageOptions> options)
    {
        var messageOptions = new MessageOptions();
        options(messageOptions);

        return _onShowMessage?.Invoke(messageOptions) ?? throw new InvalidOperationException("No dialog callback specified.");
    }

    public Task<Message> ShowMessageBarAsync(string title)
    {
        throw new NotImplementedException();
    }

    public Task<Message> ShowMessageBarAsync(string title, MessageIntent intent)
    {
        throw new NotImplementedException();
    }

    public Task<Message> ShowMessageBarAsync(string title, MessageIntent intent, string section)
    {
        throw new NotImplementedException();
    }

    public Task<Message> ShowMessageBarAsync(MarkupString title, MessageIntent intent, string section)
    {
        throw new NotImplementedException();
    }
}
