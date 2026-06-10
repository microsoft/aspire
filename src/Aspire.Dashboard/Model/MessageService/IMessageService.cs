// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Compatibility shim for the removed FluentUI v4 IMessageService.
/// Allows programmatic display of message bars.
/// </summary>
public interface IMessageService
{
    Task<Message> ShowMessageBarAsync(Action<MessageOptions> configure);
}

/// <summary>
/// Options for displaying a message bar.
/// </summary>
public sealed class MessageOptions
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public MessageBarIntent Intent { get; set; }
    public string? Section { get; set; }
    public bool AllowDismiss { get; set; }
    public bool UseMarkupString { get; set; }
    public Func<Message, Task>? OnClose { get; set; }
    public MessageLink? Link { get; set; }
    public ActionButton<Message>? PrimaryAction { get; set; }
    public ActionButton<Message>? SecondaryAction { get; set; }
}

/// <summary>
/// Represents a link in a message bar.
/// </summary>
public sealed class MessageLink
{
    public string? Text { get; set; }
    public string? Href { get; set; }
    public string? Target { get; set; }
}

/// <summary>
/// Represents a displayed message bar that can be closed programmatically.
/// </summary>
public sealed class Message
{
    private Action? _closeAction;

    internal Message(Action? closeAction = null)
    {
        _closeAction = closeAction;
    }

    public void Close()
    {
        _closeAction?.Invoke();
        _closeAction = null;
    }
}
