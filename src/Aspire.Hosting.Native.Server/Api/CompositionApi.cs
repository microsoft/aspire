// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Sessions;
using System.Collections.Immutable;

namespace Aspire.Hosting.Native.Api;

/// <summary>Creates an application session owned by the authenticated caller.</summary>
internal static class CompositionApi
{
    /// <summary>Creates a session for successive application composition generations.</summary>
    [AspireExport]
    public static CompositionSession CreateSession() => new();
}

/// <summary>Owns successive composition generations without exposing implementation services.</summary>
[AspireExport]
internal sealed class CompositionSession : ICapabilityOwner
{
    private readonly Lock _gate = new();
    private readonly ApplicationSession _session = new();
    private Composition? _composition;
    private bool _retiring;
    internal RuntimeInvitations Invitations { get; }

    internal CompositionSession() : this(new RuntimeInvitations())
    {
    }

    internal CompositionSession(RuntimeInvitations invitations)
    {
        Invitations = invitations;
    }

    /// <summary>Starts composition after the previous generation has been retired.</summary>
    [AspireExport]
    public Composition StartGeneration()
    {
        lock (_gate)
        {
            if (_retiring)
            {
                throw new InvalidOperationException("The previous execution is still retiring.");
            }
            _composition = new Composition(this, _session.StartGeneration());

            return _composition;
        }
    }

    /// <summary>Retires this session's generation and revokes its resource capabilities.</summary>
    [AspireExport]
    public async Task RetireGeneration(Composition composition)
    {
        lock (_gate)
        {
            if (_retiring || !ReferenceEquals(_composition, composition))
            {
                throw new ArgumentException("The composition is not active in this session.", nameof(composition));
            }
            _session.RetireGeneration(composition.Model.GenerationId);
            _retiring = true;
            composition.MarkRetired();
        }
        // Revoke authority before cleanup, but do not permit the next
        // generation to start until its predecessor's workloads are removed.
        await composition.ExecutionCompletion.ConfigureAwait(false);
        lock (_gate)
        {
            _composition = null;
            _retiring = false;
        }
    }

    /// <summary>Closes the session and retires its active composition.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _session.Dispose();
            _composition?.MarkRetired();
            _composition = null;
        }
    }
}

/// <summary>Declares and inspects resources within one server-owned generation.</summary>
[AspireExport]
internal sealed class Composition(CompositionSession owner, ApplicationModel model) : ICapabilityLifetime, ICapabilityOwner
{
    private int _retired;
    internal CompositionSession Owner { get; } = owner;
    internal ApplicationModel Model { get; } = model;
    internal bool IsRetired => Volatile.Read(ref _retired) != 0;
    public bool IsRevoked => IsRetired;
    private ApplicationExecution? _execution;
    private readonly Lock _runtimeGate = new();
    internal ApplicationWorkspace? Workspace { get; set; }
    internal Task ExecutionCompletion => _execution?.Runtime.Completion ?? Task.CompletedTask;
    internal void MarkRetired(bool retireExecution = true)
    {
        lock (_runtimeGate)
        {
            Interlocked.Exchange(ref _retired, 1);
            if (retireExecution)
            {
                _execution?.Retire();
            }
        }
    }

    /// <summary>Seals declarations and creates server-owned resource execution scopes.</summary>
    [AspireExport]
    public ApplicationExecution CreateExecution()
    {
        lock (_runtimeGate)
        {
            ObjectDisposedException.ThrowIf(IsRetired, this);
            if (Workspace is not null)
            {
                throw new InvalidOperationException("Commit the workspace revision before obtaining its execution.");
            }
            if (_execution is not null)
            {
                throw new InvalidOperationException("An execution already exists for this composition.");
            }
            _execution = new ApplicationExecution(this, Owner.Invitations);

            return _execution;
        }
    }

    internal ApplicationExecution AdoptExecution(Runtime.RuntimeGeneration runtime)
    {
        lock (_runtimeGate)
        {
            ObjectDisposedException.ThrowIf(IsRetired, this);
            _execution = new ApplicationExecution(this, Owner.Invitations, runtime);

            return _execution;
        }
    }

    public void Close() => Workspace?.DiscardRevision(this);

    /// <summary>Adds a resource declaration without executing a workload.</summary>
    [AspireExport]
    public DeclaredResource AddResource(string name, string typeId) => new(this, Model.AddResource(name, typeId));

    /// <summary>Reads a copied declaration snapshot without sealing composition.</summary>
    [AspireExport]
    public CompositionSnapshot Inspect() => Copy(Model.Inspect());

    /// <summary>Seals declarations and returns a copied snapshot.</summary>
    [AspireExport]
    public CompositionSnapshot Seal() => Copy(Model.Seal());

    private static CompositionSnapshot Copy(ApplicationSnapshot snapshot) => new()
    {
        GenerationId = snapshot.GenerationId.ToString(),
        Resources = snapshot.Resources.Select(DeclaredResource.Copy).ToArray()
    };
}

/// <summary>Provides declaration operations on an authoritative resource identity.</summary>
[AspireExport]
internal sealed class DeclaredResource(Composition composition, ResourceHandle handle) : ICapabilityLifetime
{
    internal Composition Composition { get; } = composition;
    internal ResourceHandle Handle { get; } = handle;
    public bool IsRevoked => Composition.IsRetired;

    /// <summary>Declares a readiness dependency within this composition.</summary>
    [AspireExport]
    public void WaitFor(DeclaredResource dependency)
    {
        if (!ReferenceEquals(Composition, dependency.Composition))
        {
            throw new ArgumentException("The dependency belongs to a different composition.", nameof(dependency));
        }
        Composition.Model.AddDependency(Handle, dependency.Handle);
    }

    /// <summary>Reads copied resource declarations without returning live implementation objects.</summary>
    [AspireExport]
    public DeclarationSnapshot InspectResource() => Copy(Composition.Model.ReadResource(Handle));

    /// <summary>Declares integration configuration for this resource in the staged model.</summary>
    [AspireExport]
    public void SetResourceConfiguration(ResourceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var properties = configuration.Properties;
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Any(property => property is null))
        {
            throw new ArgumentException("Configuration properties cannot contain null.", nameof(configuration));
        }
        Composition.Model.SetConfiguration(Handle, properties.Select(property =>
            new ResourceConfigurationEntry(property.Name, property.Value)).ToImmutableArray());
    }

    internal static DeclarationSnapshot Copy(ResourceSnapshot snapshot) => new()
    {
        ResourceId = snapshot.Handle.ResourceId.ToString(),
        Name = snapshot.Name,
        TypeId = snapshot.TypeId,
        Dependencies = snapshot.Dependencies.Select(dependency => dependency.ResourceId.ToString()).ToArray(),
        Configuration = snapshot.Configuration.Select(entry =>
            new ResourceConfigurationProperty { Name = entry.Name, Value = entry.Value }).ToArray()
    };
}

/// <summary>Contains a copied composition view, not live resource capabilities.</summary>
[AspireDto]
internal sealed class CompositionSnapshot
{
    public required string GenerationId { get; init; }
    public required DeclarationSnapshot[] Resources { get; init; }
}

/// <summary>Contains copied resource data; identifiers do not confer handle authority.</summary>
[AspireDto]
internal sealed class DeclarationSnapshot
{
    public required string ResourceId { get; init; }
    public required string Name { get; init; }
    public required string TypeId { get; init; }
    public required string[] Dependencies { get; init; }
    public required ResourceConfigurationProperty[] Configuration { get; init; }
}

/// <summary>Contains an integration-owned configuration field, not a runtime handle.</summary>
[AspireDto]
internal sealed class ResourceConfigurationProperty
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

/// <summary>Contains a complete, bounded integration configuration declaration.</summary>
[AspireDto]
internal sealed class ResourceConfiguration
{
    public required ResourceConfigurationProperty[] Properties { get; init; }
}

/// <summary>Allows the handle registry to release capabilities revoked by their owner.</summary>
internal interface ICapabilityLifetime
{
    bool IsRevoked { get; }
}

/// <summary>Releases connection-owned capabilities without exporting framework lifetime interfaces.</summary>
internal interface ICapabilityOwner
{
    void Close();
}
