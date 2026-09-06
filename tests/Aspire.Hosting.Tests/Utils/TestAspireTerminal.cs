// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Utils;

internal sealed class TestAspireTerminal(string id) : IAspireTerminal
{
    public string Id { get; } = id;
    public string Title => "Test terminal";
    public TerminalOwner Owner => TerminalOwner.AppHost;
    public TerminalPlacement Placement => TerminalPlacement.Dialog;
    public bool IsDisposed { get; private set; }
    public Func<ValueTask>? OnDispose { get; set; }

    public void Start() => throw new NotSupportedException();
    public void Show() => throw new NotSupportedException();
    public Task SendTextAsync(string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SendKeyAsync(AspireTerminalKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task WaitForTextAsync(string text, TimeSpan? timeout = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public string GetScreenText() => throw new NotSupportedException();

    // An implementation may compare terminals by ID, but that must not make it interchangeable with the
    // registered instance: the dashboard attaches to the registered object, not the supplied implementation.
    public override bool Equals(object? obj) => obj is IAspireTerminal other && string.Equals(Id, other.Id, StringComparison.Ordinal);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return OnDispose?.Invoke() ?? ValueTask.CompletedTask;
    }
}
