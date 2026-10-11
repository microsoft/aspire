// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Diagnostics;

namespace Aspire.Hosting.Native.Sessions;

/// <summary>Serializes ownership of successive declaration generations within an AppHost session.</summary>
internal sealed class ApplicationSession : IDisposable
{
    private readonly Lock _gate = new();
    private ApplicationModel? _model;
    private bool _disposed;

    /// <summary>Gets the identity used to correlate session ownership diagnostics.</summary>
    public Guid SessionId { get; } = Guid.NewGuid();

    /// <summary>Starts a generation only after the previous declaration graph has been retired.</summary>
    public ApplicationModel StartGeneration() =>
        NativeDiagnostics.Execute("session.generation.start", SessionId, null, operation => StartGenerationCore(operation));

    private ApplicationModel StartGenerationCore(NativeDiagnostics.Operation operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_model is not null)
            {
                throw new InvalidOperationException("An application generation is already active.");
            }
            _model = new ApplicationModel();
            operation.SetGeneration(_model.GenerationId);

            return _model;
        }
    }

    /// <summary>Retires the named generation without permitting a late owner to retire its replacement.</summary>
    public void RetireGeneration(Guid generationId) =>
        NativeDiagnostics.Execute("session.generation.retire", SessionId, generationId, _ => RetireGenerationCore(generationId));

    private void RetireGenerationCore(Guid generationId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_model is null || _model.GenerationId != generationId)
            {
                throw new InvalidOperationException("The application generation is not active in this session.");
            }
            _model.Dispose();
            _model = null;
        }
    }

    /// <summary>Closes the session and invalidates its active declaration generation.</summary>
    public void Dispose() =>
        NativeDiagnostics.Execute("session.close", SessionId, null, _ => DisposeCore());

    private void DisposeCore()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _model?.Dispose();
            _model = null;
        }
    }
}
