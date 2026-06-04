// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Tracks the live mapping from session id to its open <see cref="WindowHandle"/>
/// so menu activations can focus an existing window instead of opening a
/// duplicate. Lives at workspace scope (not on <c>AppState</c>) because
/// <see cref="WindowHandle"/> is a Hex1b type and the state layer is kept
/// Hex1b-free so the future <c>self-test</c> command can drive state without a TTY.
/// </summary>
internal sealed class SessionWindowRegistry
{
    private readonly Dictionary<string, WindowHandle> _bySession = new(StringComparer.Ordinal);

    public bool TryGet(string sessionId, out WindowHandle handle) =>
        _bySession.TryGetValue(sessionId, out handle!);

    public void Register(string sessionId, WindowHandle handle) =>
        _bySession[sessionId] = handle;

    public void Unregister(string sessionId) =>
        _bySession.Remove(sessionId);

    public int OpenCount => _bySession.Count;
}
