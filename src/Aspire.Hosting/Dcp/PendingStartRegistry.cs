// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp;

/// <summary>The outcome of canceling the pending start of a DCP resource instance.</summary>
internal enum PendingStartCancellation
{
    /// <summary>No start of the instance was pending.</summary>
    None,

    /// <summary>A pending start was canceled before it reached DCP, so the instance doesn't start.</summary>
    Canceled,

    /// <summary>
    /// The instance has something to stop: a start of it reached DCP, and no start has begun to delete the object that it
    /// created or started, or DCP reports the instance as running. Any starts still pending were canceled; stopping the
    /// instance stops what's running.
    /// </summary>
    Committed,
}

/// <summary>
/// Tracks the starts of DCP resource instances that haven't reached DCP yet, so that a Stop can cancel them.
/// </summary>
/// <remarks>
/// <para>
/// A start waits for the <c>BeforeResourceStartedEvent</c> handlers (WaitFor dependencies, the watch build barrier and
/// integration work) before <see cref="DcpExecutor"/> creates the instance's DCP object, or sets <c>Spec.Start</c> on an
/// explicit-start object that already exists. That is the start's commit. Until then nothing was launched, so a
/// cancellation leaves the instance <c>NotStarted</c>. After it, a Stop stops what the start launched.
/// </para>
/// <para>
/// The replicas that a resource creates at startup share one wait, so they form one group. Canceling a replica takes it
/// out of the group's start; the group's wait itself is canceled only when every replica is.
/// </para>
/// </remarks>
internal sealed class PendingStartRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<PendingStartGroup.Member>> _pending = new(StringComparers.ResourceName);
    // Instances whose most recent start was canceled before it reached DCP and that haven't started since.
    private readonly HashSet<string> _canceled = new(StringComparers.ResourceName);
    // Instances that have a DCP object: one that a committed start created, or a delayed (Spec.Start = false) one.
    private readonly HashSet<string> _withObject = new(StringComparers.ResourceName);
    // Instances whose DCP object a committed start created or started, until the object is gone.
    private readonly HashSet<string> _launched = new(StringComparers.ResourceName);
    // Instances whose DCP object a start is deleting, to replace it.
    private readonly HashSet<string> _deleting = new(StringComparers.ResourceName);
    // How many starts of each instance reached DCP, so that a canceled start can tell whether another one did meanwhile.
    private readonly Dictionary<string, long> _commits = new(StringComparers.ResourceName);

    /// <summary>
    /// Registers the starts of instances that share one wait: one instance for a Start command, or the replicas that
    /// a resource creates together at startup. Disposing the group ends the starts.
    /// </summary>
    public PendingStartGroup Begin(IReadOnlyList<string> dcpResourceNames, CancellationToken cancellationToken)
    {
        var group = new PendingStartGroup(this, dcpResourceNames, cancellationToken);
        lock (_lock)
        {
            foreach (var member in group.Members)
            {
                if (!_pending.TryGetValue(member.Name, out var members))
                {
                    _pending[member.Name] = members = [];
                }
                members.Add(member);
                member.CommitsSeen = _commits.GetValueOrDefault(member.Name);
                // A new start supersedes a canceled one.
                _canceled.Remove(member.Name);
            }
        }
        return group;
    }

    /// <summary>
    /// Cancels the starts of <paramref name="dcpResourceName"/> that haven't reached DCP.
    /// </summary>
    /// <param name="dcpResourceName">The DCP resource name of the instance.</param>
    /// <param name="running">
    /// Whether DCP reports the instance as running although none of its pending starts reached DCP. The first start of
    /// an explicit-start persistent resource starts the object that the AppHost created without starting it, and DCP may
    /// have found that resource already running.
    /// </param>
    /// <param name="groupsToCancel">The groups whose wait must be canceled because all of their starts were canceled.</param>
    /// <param name="unwound">Completes when those groups' starts have ended.</param>
    /// <param name="replacement">
    /// Set when a canceled start is deleting the instance's object to replace it. A Stop doesn't interrupt the deletion,
    /// so nothing runs once it completes; if it fails, the object may still be there to stop (see
    /// <see cref="CanceledReplacement.LeftObjectToStop"/>).
    /// </param>
    public PendingStartCancellation Cancel(string dcpResourceName, bool running, out PendingStartGroup[] groupsToCancel, out Task unwound, out CanceledReplacement? replacement)
    {
        lock (_lock)
        {
            groupsToCancel = [];
            unwound = Task.CompletedTask;
            replacement = null;
            if (!_pending.TryGetValue(dcpResourceName, out var members) || members.Count == 0)
            {
                return PendingStartCancellation.None;
            }

            // A start that reached DCP created or started the instance's object, and a queued start (for example, a Start
            // command waiting for the AppHost's startup of the instance) would replace it, but hasn't begun to delete it
            // yet. The same goes for a start of an instance that DCP reports as running. Cancel the queued starts; the
            // caller stops what's running. What DCP reports then determines the instance's state, not the canceled starts.
            var replacing = _deleting.Contains(dcpResourceName);
            var committed = running || _launched.Contains(dcpResourceName) && !replacing;
            var groups = new List<PendingStartGroup>();
            foreach (var member in members.Where(member => !member.Committed))
            {
                member.Canceled = true;
                member.Superseded = committed;
                if (member.Group.Members.All(other => other.Canceled) && !groups.Contains(member.Group))
                {
                    groups.Add(member.Group);
                }
            }
            if (!committed)
            {
                _canceled.Add(dcpResourceName);
                if (replacing)
                {
                    replacement = new CanceledReplacement(this, dcpResourceName, _commits.GetValueOrDefault(dcpResourceName));
                }
            }
            groupsToCancel = [.. groups];
            unwound = Task.WhenAll(groups.Select(group => group.Unwound));
            return committed ? PendingStartCancellation.Committed : PendingStartCancellation.Canceled;
        }
    }

    /// <summary>Whether the most recent start of the instance was canceled before it reached DCP, and it hasn't started since.</summary>
    public bool IsCanceled(string dcpResourceName)
    {
        lock (_lock)
        {
            return _canceled.Contains(dcpResourceName);
        }
    }

    /// <summary>Whether the instance has a DCP object, which a Stop can act on.</summary>
    public bool HasObject(string dcpResourceName)
    {
        lock (_lock)
        {
            return _withObject.Contains(dcpResourceName);
        }
    }

    /// <summary>Whether the instance has a DCP object that a start created or started, and no start is deleting it.</summary>
    public bool IsLaunched(string dcpResourceName)
    {
        lock (_lock)
        {
            return _launched.Contains(dcpResourceName) && !_deleting.Contains(dcpResourceName);
        }
    }

    /// <summary>
    /// Whether the object that a canceled start failed to delete is still there for the Stop to stop: no start is deleting
    /// it, and no newer start of the instance took over, which replaces it. A newer start is one that's in progress, or
    /// that reached DCP after the Stop canceled the earlier start.
    /// </summary>
    internal bool LeftObjectToStop(string dcpResourceName, long commitsAtCancel)
    {
        lock (_lock)
        {
            return !HasActiveStart(dcpResourceName, except: null) &&
                _commits.GetValueOrDefault(dcpResourceName) == commitsAtCancel &&
                _launched.Contains(dcpResourceName) && !_deleting.Contains(dcpResourceName);
        }
    }

    /// <summary>Records a DCP object that was created without starting the instance (<c>Spec.Start = false</c>).</summary>
    public void ObjectCreated(string dcpResourceName)
    {
        lock (_lock)
        {
            _withObject.Add(dcpResourceName);
        }
    }

    /// <summary>
    /// Records that a start began to delete the instance's DCP object, which it replaces. A Stop doesn't interrupt the
    /// deletion, so a Stop that cancels the start leaves nothing running once the deletion completes. Disposing the
    /// returned deletion without completing it records that the deletion failed, so the object may still run.
    /// </summary>
    public ObjectDeletion BeginDeletion(string dcpResourceName)
    {
        lock (_lock)
        {
            _deleting.Add(dcpResourceName);
        }
        return new ObjectDeletion(this, dcpResourceName);
    }

    internal void DeletionFailed(string dcpResourceName)
    {
        lock (_lock)
        {
            _deleting.Remove(dcpResourceName);
        }
    }

    /// <summary>Records that the instance's DCP object is gone, for example because a start deleted it to re-create it.</summary>
    public void ObjectDeleted(string dcpResourceName)
    {
        lock (_lock)
        {
            _withObject.Remove(dcpResourceName);
            _launched.Remove(dcpResourceName);
            _deleting.Remove(dcpResourceName);
        }
    }

    internal bool TryCommit(PendingStartGroup.Member member)
    {
        lock (_lock)
        {
            // A start whose own token was canceled (by its caller, or because the app is stopping) doesn't commit either.
            if (member.Canceled || member.Group.Token.IsCancellationRequested)
            {
                return false;
            }
            member.Committed = true;
            _withObject.Add(member.Name);
            _launched.Add(member.Name);
            _commits[member.Name] = _commits.GetValueOrDefault(member.Name) + 1;
            // The instance's most recent start is this one, which wasn't canceled.
            _canceled.Remove(member.Name);
            return true;
        }
    }

    /// <summary>
    /// Records that a start ended because it was canceled before it reached DCP, unless something else determines the
    /// instance's state: another start of it reached DCP meanwhile or is still in progress, an object that an earlier
    /// start launched still exists (the canceled start didn't delete it, or its deletion failed), or the Stop that
    /// canceled the start found the instance running.
    /// </summary>
    /// <returns><see langword="true"/> if the instance didn't start.</returns>
    internal bool TryEndCanceled(PendingStartGroup.Member member)
    {
        lock (_lock)
        {
            // For example, a Start command whose caller gave up while it waited behind another start of the instance.
            if (member.Superseded || HasActiveStart(member.Name, except: member) || _launched.Contains(member.Name) ||
                _commits.GetValueOrDefault(member.Name) != member.CommitsSeen)
            {
                return false;
            }
            _canceled.Add(member.Name);
            return true;
        }
    }

    /// <summary>
    /// Records that the start holds the instance's lock. The starts that it waited behind have ended, and what they
    /// launched is what it replaces, so their commits don't mean that another start determines the instance's state.
    /// </summary>
    internal void HoldsInstanceLock(PendingStartGroup.Member member)
    {
        lock (_lock)
        {
            member.CommitsSeen = _commits.GetValueOrDefault(member.Name);
        }
    }

    /// <summary>
    /// Whether a start of the instance, other than <paramref name="except"/>, determines its state: it reached DCP and
    /// hasn't ended, or it's in progress. A start that a Stop or its own caller canceled, and that didn't reach DCP, won't
    /// start the instance. Call under the lock.
    /// </summary>
    private bool HasActiveStart(string dcpResourceName, PendingStartGroup.Member? except) =>
        _pending.TryGetValue(dcpResourceName, out var members) &&
        members.Any(other => !ReferenceEquals(other, except) &&
            (other.Committed || !other.Canceled && !other.Group.Token.IsCancellationRequested));

    internal bool IsCanceled(PendingStartGroup.Member member)
    {
        lock (_lock)
        {
            return member.Canceled;
        }
    }

    internal bool IsCommitted(PendingStartGroup.Member member)
    {
        lock (_lock)
        {
            return member.Committed;
        }
    }

    internal void End(PendingStartGroup group)
    {
        lock (_lock)
        {
            foreach (var member in group.Members)
            {
                if (_pending.TryGetValue(member.Name, out var members))
                {
                    members.Remove(member);
                    if (members.Count == 0)
                    {
                        _pending.Remove(member.Name);
                    }
                }
            }
        }
    }
}

/// <summary>The deletion of an instance's DCP object by a start that replaces it; see <see cref="PendingStartRegistry.BeginDeletion"/>.</summary>
internal sealed class ObjectDeletion(PendingStartRegistry registry, string dcpResourceName) : IDisposable
{
    private bool _completed;

    /// <summary>Records that the object is gone.</summary>
    public void Complete()
    {
        _completed = true;
        registry.ObjectDeleted(dcpResourceName);
    }

    public void Dispose()
    {
        if (!_completed)
        {
            registry.DeletionFailed(dcpResourceName);
        }
    }
}

/// <summary>
/// A start that a Stop canceled while it deleted the instance's object to replace it; see
/// <see cref="PendingStartRegistry.Cancel"/>.
/// </summary>
internal sealed class CanceledReplacement(PendingStartRegistry registry, string dcpResourceName, long commitsAtCancel)
{
    /// <summary>
    /// Whether the Stop has to stop the instance's object once the canceled start has ended: the start's deletion failed,
    /// so the object may still run, and no newer start of the instance took over, which would replace it.
    /// </summary>
    public bool LeftObjectToStop() => registry.LeftObjectToStop(dcpResourceName, commitsAtCancel);
}

/// <summary>Starts that share one wait; see <see cref="PendingStartRegistry"/>.</summary>
internal sealed class PendingStartGroup : IDisposable
{
    private readonly PendingStartRegistry _registry;
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource _unwound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingStartGroup(PendingStartRegistry registry, IReadOnlyList<string> dcpResourceNames, CancellationToken cancellationToken)
    {
        _registry = registry;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Kept, so that other starts can check it after this group's source was disposed.
        Token = _cancellation.Token;
        Members = [.. dcpResourceNames.Select(name => new Member(name, this))];
    }

    /// <summary>
    /// Cancels when the caller's token is canceled, or when every start of the group was canceled. Pass it to everything
    /// that the starts wait for before they reach DCP.
    /// </summary>
    public CancellationToken Token { get; }

    internal IReadOnlyList<Member> Members { get; }

    internal Task Unwound => _unwound.Task;

    /// <summary>Commits the start of <paramref name="dcpResourceName"/>; it can't be canceled after this.</summary>
    /// <returns><see langword="false"/> if the start was canceled, or the group's token was.</returns>
    public bool TryCommit(string dcpResourceName) => _registry.TryCommit(GetMember(dcpResourceName));

    /// <summary>Whether a Stop canceled the start of <paramref name="dcpResourceName"/>.</summary>
    public bool IsCanceled(string dcpResourceName) => _registry.IsCanceled(GetMember(dcpResourceName));

    /// <summary>Whether the start of <paramref name="dcpResourceName"/> reached DCP.</summary>
    public bool IsCommitted(string dcpResourceName) => _registry.IsCommitted(GetMember(dcpResourceName));

    /// <summary>
    /// Ends the start of <paramref name="dcpResourceName"/>, which was canceled before it reached DCP.
    /// </summary>
    /// <returns><see langword="false"/> if another start of the instance reached DCP meanwhile and determines its state.</returns>
    public bool TryEndCanceled(string dcpResourceName) => _registry.TryEndCanceled(GetMember(dcpResourceName));

    /// <summary>
    /// Records that the start of <paramref name="dcpResourceName"/> holds the instance's lock, so the object that it
    /// replaces or starts is the one that the starts it waited behind launched.
    /// </summary>
    public void HoldsInstanceLock(string dcpResourceName) => _registry.HoldsInstanceLock(GetMember(dcpResourceName));

    internal void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The starts ended (for example, they found themselves canceled at their commit) after the registry
            // handed out the group and before it was canceled. There's nothing left to cancel.
        }
    }

    public void Dispose()
    {
        _registry.End(this);
        _unwound.TrySetResult();
        _cancellation.Dispose();
    }

    private Member GetMember(string dcpResourceName) =>
        Members.First(member => string.Equals(member.Name, dcpResourceName, StringComparisons.ResourceName));

    internal sealed class Member(string name, PendingStartGroup group)
    {
        public string Name { get; } = name;

        public PendingStartGroup Group { get; } = group;

        // Guarded by the registry's lock.
        public bool Canceled { get; set; }

        // A Stop canceled the start while the instance had something to stop, which then determines the instance's state.
        public bool Superseded { get; set; }

        public bool Committed { get; set; }

        // The instance's commits when the start began, or when it took the instance's lock. Later ones are by other starts.
        public long CommitsSeen { get; set; }
    }
}
