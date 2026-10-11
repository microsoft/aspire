// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Api;

/// <summary>Owns an application runtime independently of AppHost connections and composition revisions.</summary>
[AspireExport]
internal sealed class ApplicationWorkspace(RuntimeInvitations invitations) : ICapabilityLifetime
{
    private readonly Lock _gate = new();
    private readonly CompositionSession _owner = new(invitations);
    private Composition? _pending;
    private Composition? _committed;
    private ApplicationExecution? _execution;
    private bool _applying;
    private bool _closed;
    private Exception? _applyFailure;
    public bool IsRevoked => Volatile.Read(ref _closed);

    /// <summary>Delegates access to a subsequent AppHost connection through a one-use invitation.</summary>
    [AspireExport]
    public string InviteApplicationWorkspace() => invitations.Issue(this, () => this);

    /// <summary>Begins an isolated desired-model revision while the committed application keeps running.</summary>
    [AspireExport]
    public Composition BeginRevision()
    {
        lock (_gate)
        {
            EnsureAvailable();
            if (_pending is not null)
            {
                throw new InvalidOperationException("An application revision is already being composed.");
            }
            _pending = new Composition(_owner, new ApplicationModel()) { Workspace = this };

            return _pending;
        }
    }

    /// <summary>Discards an uncommitted revision without changing running resources.</summary>
    [AspireExport]
    public void AbortRevision(Composition revision)
    {
        lock (_gate)
        {
            EnsureAvailable();
            ValidatePending(revision);
            DiscardRevision(revision);
        }
    }

    internal void DiscardRevision(Composition revision)
    {
        lock (_gate)
        {
            if (_applying || !ReferenceEquals(_pending, revision))
            {
                return;
            }
            revision.MarkRetired();
            revision.Model.Dispose();
            _pending = null;
        }
    }

    /// <summary>Commits desired declarations, retains compatible resource executions, and awaits removed workload cleanup.</summary>
    [AspireExport]
    public async Task CommitRevision(Composition revision)
    {
        Task cleanup;
        using var operation = new NativeDiagnostics.Operation("runtime.revision.commit", null, revision.Model.GenerationId);
        lock (_gate)
        {
            EnsureAvailable();
            ValidatePending(revision);
            var declarations = revision.Model.Seal();
            RuntimeGeneration runtime;
            if (_execution is null)
            {
                runtime = new RuntimeGeneration(declarations, invitations.Executor);
                invitations.Track(runtime);
                cleanup = Task.CompletedTask;
            }
            else
            {
                runtime = _execution.Runtime;
                cleanup = runtime.ApplyRevision(declarations);
            }
            _applying = true;
            _committed?.MarkRetired(retireExecution: false);
            _committed?.Model.Dispose();
            _committed = revision;
            _pending = null;
            _execution = revision.AdoptExecution(runtime);
        }
        try
        {
            await cleanup.ConfigureAwait(false);
            operation.Succeeded = true;
        }
        catch (Exception exception)
        {
            // The desired revision is committed even when external cleanup fails.
            // Surface the failure; never claim external side effects rolled back.
            operation.ErrorType = exception.GetType().FullName;
            lock (_gate)
            {
                _applyFailure = exception;
            }
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _applying = false;
            }
        }
    }

    /// <summary>Obtains authoring authority for the currently committed runtime revision.</summary>
    [AspireExport]
    public ApplicationExecution GetApplicationExecution()
    {
        lock (_gate)
        {
            EnsureAvailable();

            return _execution ?? throw new InvalidOperationException("No application revision has been committed.");
        }
    }

    /// <summary>Explicitly retires the workspace and awaits cleanup independently of AppHost connection lifetime.</summary>
    [AspireExport]
    public async Task RetireApplicationWorkspace()
    {
        Close();
        if (_execution is not null)
        {
            await _execution.Runtime.Completion.ConfigureAwait(false);
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            _pending?.MarkRetired();
            _pending?.Model.Dispose();
            _pending = null;
            _committed?.MarkRetired();
            _committed?.Model.Dispose();
        }
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_applyFailure is not null)
        {
            throw new InvalidOperationException("The committed revision failed to clean up obsolete workloads; retire the workspace before retrying.",
                _applyFailure);
        }
        if (_applying)
        {
            throw new InvalidOperationException("The previous revision is still being applied.");
        }
    }

    private void ValidatePending(Composition revision)
    {
        if (!ReferenceEquals(_pending, revision))
        {
            throw new ArgumentException("The revision is not staged in this workspace.", nameof(revision));
        }
    }
}
