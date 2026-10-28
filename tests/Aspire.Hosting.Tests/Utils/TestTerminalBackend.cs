// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRETERMINAL001 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Utils;

internal sealed class TestTerminalBackend(string id) : ITerminalBackend
{
    public string Id { get; } = id;
    public string Title => "Test terminal";
    public TerminalOwner Owner => TerminalOwner.AppHost;
    public TerminalPlacement Placement => TerminalPlacement.Dialog;
    public bool IsDisposed { get; private set; }
    public Func<ValueTask>? OnDispose { get; set; }
    public Func<AspireTerminalKey, CancellationToken, Task>? OnSendKey { get; set; }
    public Action? OnStart { get; set; }
    public Action? OnShow { get; set; }
    public Func<string, CancellationToken, Task>? OnSendText { get; set; }
    public Func<string, TimeSpan?, CancellationToken, Task>? OnWaitForText { get; set; }
    public Func<string>? OnGetScreenText { get; set; }

    public void Start() => (OnStart ?? throw new NotSupportedException())();
    public void Show() => (OnShow ?? throw new NotSupportedException())();
    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        => OnSendText?.Invoke(text, cancellationToken) ?? throw new NotSupportedException();
    public Task SendKeyAsync(AspireTerminalKey key, CancellationToken cancellationToken = default)
        => OnSendKey?.Invoke(key, cancellationToken) ?? throw new NotSupportedException();
    public Task WaitForTextAsync(string text, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => OnWaitForText?.Invoke(text, timeout, cancellationToken) ?? throw new NotSupportedException();
    public string GetScreenText() => OnGetScreenText?.Invoke() ?? throw new NotSupportedException();

    // Backend equality must not make distinct public handles interchangeable.
    public override bool Equals(object? obj) => obj is ITerminalBackend other && string.Equals(Id, other.Id, StringComparison.Ordinal);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return OnDispose?.Invoke() ?? ValueTask.CompletedTask;
    }
}
