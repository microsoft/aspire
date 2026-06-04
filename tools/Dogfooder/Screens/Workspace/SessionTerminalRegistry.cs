// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Tracks the live PTY-backed <see cref="Hex1bTerminal"/> instances keyed by
/// session id. Pulled out of the per-frame content builders because the
/// terminal lifecycle must outlive any single render pass: Build is called
/// every frame, but we must only spawn the PTY once per session and reuse it
/// on every subsequent render of that window.
/// </summary>
internal sealed class SessionTerminalRegistry
{
    public sealed record Entry(
        Hex1bTerminal Terminal,
        TerminalWidgetHandle Handle,
        CancellationTokenSource Cts);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string sessionId, out Entry entry) =>
        _entries.TryGetValue(sessionId, out entry!);

    public void Register(string sessionId, Entry entry) =>
        _entries[sessionId] = entry;

    /// <summary>
    /// Tears down the terminal associated with <paramref name="sessionId"/>:
    /// cancels its run-loop and disposes the PTY. Safe to call when no entry
    /// exists (no-op). Disposal runs on a background task so callers in the
    /// render path stay synchronous.
    /// </summary>
    public void Dispose(string sessionId)
    {
        if (!_entries.Remove(sessionId, out var entry))
        {
            return;
        }

        _ = CleanupAsync(entry);
    }

    private static async Task CleanupAsync(Entry entry)
    {
        try
        {
            entry.Cts.Cancel();
            await entry.Terminal.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup; nothing actionable on shutdown failures.
        }
        finally
        {
            entry.Cts.Dispose();
        }
    }
}
