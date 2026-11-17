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
    private readonly ApplicationSession _session;
    private Composition? _composition;
    private bool _retiring;
    internal RuntimeInvitations Invitations { get; }

    internal CompositionSession() : this(new RuntimeInvitations())
    {
    }

    internal CompositionSession(RuntimeInvitations invitations)
    {
        Invitations = invitations;
        _session = new ApplicationSession(invitations.Options.TimeProvider);
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
internal sealed partial class Composition(CompositionSession owner, ApplicationModel model) : ICapabilityLifetime, ICapabilityOwner
{
    private int _retired;
    internal CompositionSession Owner { get; } = owner;
    internal ApplicationModel Model { get; } = model;
    internal bool IsRetired => Volatile.Read(ref _retired) != 0;
    public bool IsRevoked => IsRetired;
    private ApplicationExecution? _execution;
    private readonly Lock _runtimeGate = new();
    internal ApplicationWorkspace? Workspace { get; set; }
    private readonly List<IProviderResourceRetirement> _providerResources = [];
    private Task _providerRetirement = Task.CompletedTask;
    internal Task ExecutionCompletion => Task.WhenAll(_execution?.Runtime.Completion ?? Task.CompletedTask, _providerRetirement);
    internal Task ProviderRetirement => _providerRetirement;
    internal void TrackProviderResource(IProviderResourceRetirement resource)
    {
        lock (_runtimeGate)
        {
            ObjectDisposedException.ThrowIf(IsRetired, this);
            _providerResources.Add(resource);
        }
    }

    internal void MarkRetired(bool retireExecution = true, Task? providerCleanup = null)
    {
        lock (_runtimeGate)
        {
            Interlocked.Exchange(ref _retired, 1);
            if (retireExecution)
            {
                _execution?.Retire();
            }
            if (_providerResources.Count > 0)
            {
                var resources = _providerResources.ToArray();
                _providerResources.Clear();
                _providerRetirement = RetireProviderResourcesAsync(resources,
                    providerCleanup ?? _execution?.Runtime.Completion ?? Task.CompletedTask);
                _ = _providerRetirement.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private static async Task RetireProviderResourcesAsync(IProviderResourceRetirement[] resources, Task cleanup)
    {
        // ExecutionCompletion and CommitRevision independently await native
        // cleanup and report its failure. This wait only orders local release:
        // retaining the old cleanup fault here would fence a later deletion retry
        // even after all provider objects had already been released.
        await cleanup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Task.WhenAll(resources.Select(resource => resource.RetireAsync())).ConfigureAwait(false);
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

    /// <summary>Creates a literal value that remains owned by this composition.</summary>
    /// <param name="value">The literal launch data; use a secret parameter for credentials.</param>
    /// <returns>A composition-scoped symbolic value.</returns>
    [AspireExport]
    public DeclaredValue CreateLiteralValue(string value) => new(this, SymbolicValue.Literal(Model.GenerationId, value));

    /// <summary>Declares a named secret whose payload never appears in model inspection.</summary>
    /// <param name="name">The unique parameter name within this composition.</param>
    /// <param name="value">The secret supplied to the server; it is substituted only during workload launch.</param>
    /// <returns>A secret-classified composition-scoped value.</returns>
    [AspireExport]
    public DeclaredValue AddSecretParameter(string name, string value) => new(this, Model.AddSecretParameter(name, value));

    /// <summary>Generates a default credential within the native secret store without returning its payload.</summary>
    [AspireExport]
    public DeclaredValue AddGeneratedSecretParameter(string name) =>
        new(this, Model.AddSecretParameter(name, System.Security.Cryptography.RandomNumberGenerator.GetHexString(64)));

    /// <summary>References a named workload endpoint property without resolving its address during composition.</summary>
    /// <param name="resource">The endpoint's resource in this composition.</param>
    /// <param name="endpointName">The declared workload endpoint name.</param>
    /// <param name="property">One of host, port, hostAndPort, scheme, or url.</param>
    /// <returns>A late-bound resource-reference value.</returns>
    [AspireExport]
    public DeclaredValue CreateEndpointValue(DeclaredResource resource, string endpointName, string property)
    {
        if (!ReferenceEquals(this, resource.Composition))
        {
            throw new ArgumentException("The endpoint resource belongs to another composition.", nameof(resource));
        }
        var parsed = property switch
        {
            "host" => EndpointValueProperty.Host,
            "port" => EndpointValueProperty.Port,
            "hostAndPort" => EndpointValueProperty.HostAndPort,
            "scheme" => EndpointValueProperty.Scheme,
            "url" => EndpointValueProperty.Url,
            _ => throw new ArgumentException("Unsupported endpoint value property.", nameof(property))
        };

        return new(this, SymbolicValue.Endpoint(resource.Handle, endpointName, parsed));
    }

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
internal sealed partial class DeclaredResource(Composition composition, ResourceHandle handle) : ICapabilityLifetime
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
        if (properties.Any(property => property.Name == "publish.computeEnvironment"))
        {
            throw new ArgumentException("Use WithComputeEnvironment to assign a compute environment.", nameof(configuration));
        }
        Composition.Model.SetConfiguration(Handle, properties.Select(property =>
            new ResourceConfigurationEntry(property.Name, property.Value)).ToImmutableArray());
    }

    /// <summary>Stores one symbolic configuration field and automatically tracks its referenced resources.</summary>
    /// <param name="name">The integration-owned field name used by symbolic workload bindings.</param>
    /// <param name="value">A value from this composition, never a resolved secret or endpoint string.</param>
    [AspireExport]
    public void SetSymbolicConfiguration(string name, DeclaredValue value)
    {
        if (!ReferenceEquals(Composition, value.Composition))
        {
            throw new ArgumentException("The value belongs to another composition.", nameof(value));
        }
        Composition.Model.SetSymbolicConfiguration(Handle, name, value.Value);
    }

    /// <summary>Removes a symbolic field and any value dependencies no longer referenced by the resource.</summary>
    [AspireExport]
    public void RemoveSymbolicConfiguration(string name) => Composition.Model.RemoveSymbolicConfiguration(Handle, name);

    /// <summary>Inspects symbolic metadata without returning literal data, secret payloads or resolved addresses.</summary>
    [AspireExport]
    public SymbolicConfigurationSnapshot InspectSymbolicConfiguration() =>
        CopySymbolic(Composition.Model.ReadResource(Handle).SymbolicConfiguration);

    internal static SymbolicConfigurationSnapshot CopySymbolic(ImmutableArray<SymbolicConfigurationEntry> configuration) => new()
    {
        Properties = configuration.Select(entry => new SymbolicConfigurationProperty
        {
            Name = entry.Name,
            IsSecret = entry.Value.IsSecret,
            References = entry.Value.References.Select(reference => reference.ResourceId.ToString()).ToArray(),
            ParameterReferences = entry.Value.ParameterReferences.ToArray()
        }).ToArray()
    };

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

/// <summary>Owns immutable symbolic data under the same authority and retirement boundary as its composition.</summary>
[AspireExport]
internal sealed class DeclaredValue : ICapabilityLifetime
{
    internal Composition Composition { get; }
    internal SymbolicValue Value { get; }
    public bool IsRevoked => Composition.IsRetired;

    internal DeclaredValue(Composition composition, SymbolicValue value)
    {
        composition.Model.ValidateValue(value);
        Composition = composition;
        Value = value;
    }

    /// <summary>Concatenates values while retaining their resource references and secret classification.</summary>
    /// <param name="suffix">The following value in the same composition.</param>
    /// <returns>A new immutable composition-scoped value.</returns>
    [AspireExport]
    public DeclaredValue ConcatValue(DeclaredValue suffix)
    {
        if (!ReferenceEquals(Composition, suffix.Composition))
        {
            throw new ArgumentException("The value belongs to another composition.", nameof(suffix));
        }

        return new(Composition, Value.Concat(suffix.Value));
    }

    /// <summary>Escapes this value as a URI data segment during server-side resolution.</summary>
    /// <returns>A new value retaining the original references and secret classification.</returns>
    [AspireExport]
    public DeclaredValue EncodeUriValue() => new(Composition, Value.UriEncode());
}

/// <summary>Contains symbolic metadata only; no credential payloads or live capability handles.</summary>
[AspireDto]
internal sealed class SymbolicConfigurationSnapshot
{
    public required SymbolicConfigurationProperty[] Properties { get; init; }
}

/// <summary>Describes one value's secret classification and dependency identities without resolving it.</summary>
[AspireDto]
internal sealed class SymbolicConfigurationProperty
{
    public required string Name { get; init; }
    public required bool IsSecret { get; init; }
    public required string[] References { get; init; }
    public required string[] ParameterReferences { get; init; }
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
