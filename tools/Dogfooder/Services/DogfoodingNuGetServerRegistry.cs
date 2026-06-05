// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.Services;

/// <summary>
/// Per-session ownership of <see cref="DogfoodingNuGetServer"/> instances.
/// Parallels <c>SessionTerminalRegistry</c>: a session's NuGet server
/// lifecycle must outlive any single render pass (Hex1b re-invokes content
/// callbacks every frame) and must be torn down deterministically when the
/// session window closes or the app shuts down.
/// </summary>
internal sealed class DogfoodingNuGetServerRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, DogfoodingNuGetServer> _bySession = new(StringComparer.Ordinal);

    public bool TryGet(string sessionId, out DogfoodingNuGetServer server) =>
        _bySession.TryGetValue(sessionId, out server!);

    public void Register(string sessionId, DogfoodingNuGetServer server) =>
        _bySession[sessionId] = server;

    /// <summary>
    /// Tears down the server associated with <paramref name="sessionId"/>.
    /// Disposal is spun onto a background task so callers on the render path
    /// stay synchronous; the Kestrel host's graceful-stop window is short
    /// (2s) so this typically completes well before the next session is
    /// configured.
    /// </summary>
    public void Dispose(string sessionId)
    {
        if (!_bySession.Remove(sessionId, out var server))
        {
            return;
        }
        _ = SafeDisposeAsync(server);
    }

    public async ValueTask DisposeAsync()
    {
        // Snapshot the values; DisposeAsync calls may take a moment apiece so
        // we await them sequentially to keep the shutdown deterministic
        // (parallel disposal of multiple Kestrel hosts has occasionally
        // surfaced port-release races on Windows).
        var values = _bySession.Values.ToList();
        _bySession.Clear();
        foreach (var s in values)
        {
            await SafeDisposeAsync(s).ConfigureAwait(false);
        }
    }

    private static async Task SafeDisposeAsync(DogfoodingNuGetServer server)
    {
        try
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort shutdown; nothing actionable to surface.
        }
    }
}
