// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Services;

namespace Aspire.Dogfooder.State;

/// <summary>
/// In-memory collection of <see cref="DogfoodSession"/>s plus the JSON-file
/// persistence indirection. Mutations raise the shared
/// <see cref="ChangeNotifier"/> so the session-list panel re-renders.
/// </summary>
internal sealed class DogfoodSessionStore
{
    public DogfoodSessionStore(ChangeNotifier notifier, ISessionStoreFile file)
    {
        _notifier = notifier;
        _file = file;
    }

    private readonly ChangeNotifier _notifier;
    private readonly ISessionStoreFile _file;
    private readonly List<DogfoodSession> _sessions = new();

    public IReadOnlyList<DogfoodSession> Sessions => _sessions;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var loaded = await _file.LoadAsync(cancellationToken).ConfigureAwait(false);
        _sessions.Clear();
        _sessions.AddRange(loaded);
        _notifier.Notify();
    }

    public DogfoodSession Add(string name, DogfoodSessionConfig config)
    {
        var session = new DogfoodSession(
            id: Guid.NewGuid().ToString("N"),
            name: name,
            config: config);
        _sessions.Add(session);
        _notifier.Notify();
        return session;
    }

    public void Remove(DogfoodSession session)
    {
        _sessions.Remove(session);
        _notifier.Notify();
    }

    public Task SaveAsync(CancellationToken cancellationToken) =>
        _file.SaveAsync(_sessions, cancellationToken);
}
