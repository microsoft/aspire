// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.State;

/// <summary>
/// Tiny multicast-delegate-backed change notifier. Hex1b's
/// <c>Hex1bApp.Invalidate()</c> needs to be called when state mutates;
/// instead of every state class taking a dependency on the app instance,
/// state classes expose <see cref="Changed"/> and the screen layer subscribes
/// once at startup and forwards each event to <c>Invalidate()</c>. This keeps
/// state classes Hex1b-free so they're unit-testable from the future
/// <c>self-test</c> command without any TUI surface.
/// </summary>
internal sealed class ChangeNotifier
{
    public event Action? Changed;

    public void Notify() => Changed?.Invoke();
}
