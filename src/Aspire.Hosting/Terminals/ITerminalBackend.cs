// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRETERMINAL002 // Internal consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// The internal operations behind an <see cref="AspireTerminal"/> handle.
/// </summary>
internal interface ITerminalBackend : IAsyncDisposable
{
    string Id { get; }
    string Title { get; }
    TerminalOwner Owner { get; }
    TerminalPlacement Placement { get; }
    void Start();
    void Show();
    Task SendTextAsync(string text, CancellationToken cancellationToken);
    Task SendKeyAsync(AspireTerminalKey key, CancellationToken cancellationToken);
    Task WaitForTextAsync(string text, TimeSpan? timeout, CancellationToken cancellationToken);
    string GetScreenText();
}
