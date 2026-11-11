// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Authoring;

/// <summary>Projects run-mode authoring onto a server-owned composition.</summary>
[AspireExport]
internal sealed class RpcBuilder : ICapabilityLifetime, ICapabilityOwner
{
    private readonly CompositionSession _session;
    private readonly Lock _gate = new();
    private int _callbacks;
    internal Composition Composition { get; }
    public bool IsRevoked => Composition.IsRevoked;

    internal RpcBuilder(CompositionSession session)
    {
        _session = session;
        Composition = session.StartGeneration();
    }

    /// <summary>Declares a container without starting it.</summary>
    [AspireExport]
    public RpcResource AddContainer(string name, string image) => new(this, Composition.AddContainer(name, image));

    /// <summary>Declares an executable without starting it.</summary>
    [AspireExport]
    public RpcResource AddExecutable(string name, string executablePath, string workingDirectory) =>
        new(this, Composition.AddExecutable(name, executablePath, workingDirectory));

    /// <summary>Creates a symbolic secret parameter without returning its value.</summary>
    [AspireExport]
    public DeclaredValue AddSecretParameter(string name, string value) => Composition.AddSecretParameter(name, value);

    /// <summary>Returns the composition after all authoring callbacks have completed.</summary>
    [AspireExport]
    public Composition Build()
    {
        lock (_gate)
        {
            if (_callbacks != 0)
            {
                throw new InvalidOperationException("Authoring callbacks must complete before building.");
            }

            return Composition;
        }
    }

    /// <summary>Retires this builder and all resource, value and editor handles it owns.</summary>
    [AspireExport]
    public Task Retire() => _session.RetireGeneration(Composition);

    internal async Task EditAsync<T>(T editor, Func<T, Task> callback) where T : RpcEditorScope
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsRevoked, this);
            _callbacks++;
        }
        try
        {
            await callback(editor).ConfigureAwait(false);
        }
        finally
        {
            editor.Close();
            lock (_gate)
            {
                _callbacks--;
            }
        }
    }

    public void Close() => _session.Close();
}

/// <summary>Provides fluent authoring operations over a server-owned resource declaration.</summary>
[AspireExport]
internal sealed class RpcResource(RpcBuilder owner, DeclaredResource resource) : ICapabilityLifetime
{
    private const string ConnectionStringKey = "rpc.connection-string";
    private const string AnnotationPrefix = "rpc.annotation.";
    internal RpcBuilder Owner { get; } = owner;
    internal DeclaredResource Resource { get; } = resource;
    public bool IsRevoked => Resource.IsRevoked;

    /// <summary>Reads the authoritative resource name.</summary>
    [AspireExport]
    public string GetResourceName() => Resource.InspectResource().Name;

    /// <summary>Sets a literal or symbolic environment variable without resolving secrets or endpoints.</summary>
    [AspireExport]
    public RpcResource WithEnvironment(string name, [AspireUnion(typeof(string), typeof(DeclaredValue), typeof(RpcEndpoint), typeof(RpcResource))] object value)
    {
        Resource.WithEnvironment(name, Value(value));

        return this;
    }

    /// <summary>Appends literal arguments without shell parsing.</summary>
    [AspireExport]
    public RpcResource WithArgs(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Any(argument => argument is null))
        {
            throw new ArgumentException("Arguments cannot contain null.", nameof(args));
        }
        foreach (var argument in args)
        {
            Resource.WithArgument(Value(argument));
        }

        return this;
    }

    /// <summary>Declares an endpoint using copied options and server-owned allocation.</summary>
    [AspireExport]
    public RpcResource WithEndpoint(RpcEndpointOptions options)
    {
        Resource.WithEndpoint(new WorkloadEndpointDeclaration
        {
            Name = options.Name,
            TargetPort = options.TargetPort,
            Protocol = options.Protocol?.ToUpperInvariant() ?? "TCP",
            UriScheme = options.Scheme ?? "tcp",
            HostPort = options.Port ?? 0,
            EnvironmentVariable = options.Env ?? ""
        });

        return this;
    }

    /// <summary>Returns a symbolic endpoint reference, not a resolved address.</summary>
    [AspireExport]
    public RpcEndpoint GetEndpoint(string name) => new(this, name);

    /// <summary>Replaces the container image tag while preserving repository and registry ports.</summary>
    [AspireExport]
    public RpcResource WithImageTag(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (tag.Length > 128 || !(char.IsAsciiLetterOrDigit(tag[0]) || tag[0] == '_') ||
            tag.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-')))
        {
            throw new ArgumentException("Invalid container image tag.", nameof(tag));
        }
        Owner.Composition.Model.MutateWorkload(Resource.Handle, current =>
        {
            if (current.Plan is not ContainerWorkload container || container.Image.Contains('@'))
            {
                throw new InvalidOperationException("Image tags require a container without a digest.");
            }
            // In "localhost:5000/team/image:old", only the colon after the final
            // slash starts a tag. Digest-pinned images are not silently unpinned.
            var colon = container.Image.LastIndexOf(':');
            var image = colon > container.Image.LastIndexOf('/') ? container.Image[..colon] : container.Image;

            return (current with { Plan = container with { Image = $"{image}:{tag}" } }, null);
        });

        return this;
    }

    /// <summary>Declares a connection string as an authoritative symbolic field.</summary>
    [AspireExport]
    public RpcResource WithConnectionString([AspireUnion(typeof(string), typeof(DeclaredValue), typeof(RpcEndpoint), typeof(RpcResource))] object value)
    {
        Resource.SetSymbolicConfiguration(ConnectionStringKey, Value(value));

        return this;
    }

    /// <summary>Injects a producer connection string through a ConnectionStrings environment variable.</summary>
    [AspireExport]
    public RpcResource WithReference(RpcResource source)
    {
        Resource.WithEnvironment($"ConnectionStrings__{source.GetResourceName()}", Value(source));

        return this;
    }

    /// <summary>Declares a readiness dependency.</summary>
    [AspireExport]
    public RpcResource WaitFor(RpcResource dependency)
    {
        Resource.WaitFor(dependency.Resource);

        return this;
    }

    /// <summary>Stores portable annotation JSON in the declaration configuration.</summary>
    [AspireExport]
    public RpcResource WithSerializedAnnotation(string annotationId, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationId);
        using var document = JsonDocument.Parse(json);
        var key = AnnotationPrefix + annotationId;
        var configuration = Owner.Composition.Model.ReadResource(Resource.Handle).Configuration;
        Owner.Composition.Model.SetConfiguration(Resource.Handle, configuration.Where(entry => entry.Name != key)
            .Append(new ResourceConfigurationEntry(key, document.RootElement.GetRawText())).ToImmutableArray());

        return this;
    }

    /// <summary>Reads portable annotation JSON or null when the annotation is absent.</summary>
    [AspireExport]
    public string? GetSerializedAnnotation(string annotationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationId);

        return Owner.Composition.Model.ReadResource(Resource.Handle).Configuration
            .FirstOrDefault(entry => entry.Name == AnnotationPrefix + annotationId)?.Value;
    }

    /// <summary>Invokes a guest environment editor while granting authority only for this callback.</summary>
    [AspireExport]
    public async Task<RpcResource> WithEnvironmentCallback(Func<RpcEnvironmentEditor, Task> callback)
    {
        await Owner.EditAsync(new RpcEnvironmentEditor(this), callback).ConfigureAwait(false);

        return this;
    }

    /// <summary>Invokes a guest argument editor while granting authority only for this callback.</summary>
    [AspireExport]
    public async Task<RpcResource> WithArgsCallback(Func<RpcArgsEditor, Task> callback)
    {
        await Owner.EditAsync(new RpcArgsEditor(this), callback).ConfigureAwait(false);

        return this;
    }

    internal DeclaredValue Value(object value)
    {
        var result = value switch
        {
            string text => Owner.Composition.CreateLiteralValue(text),
            DeclaredValue symbolic => symbolic,
            RpcEndpoint endpoint => endpoint.Property("url"),
            RpcResource source => new DeclaredValue(source.Owner.Composition,
                source.Owner.Composition.Model.ReadResource(source.Resource.Handle).SymbolicConfiguration
                    .FirstOrDefault(entry => entry.Name == ConnectionStringKey)?.Value
                    ?? throw new InvalidOperationException("The source has no declared connection string.")),
            _ => throw new ArgumentException("Unsupported authoring value.", nameof(value))
        };
        if (!ReferenceEquals(result.Composition, Owner.Composition))
        {
            throw new ArgumentException("The value belongs to another composition.", nameof(value));
        }

        return result;
    }
}

/// <summary>Contains copied run-mode endpoint options.</summary>
[AspireDto]
internal sealed class RpcEndpointOptions
{
    public required string Name { get; init; }
    public required int TargetPort { get; init; }
    public string? Scheme { get; init; }
    public string? Protocol { get; init; }
    public int? Port { get; init; }
    public string? Env { get; init; }
}

/// <summary>Projects endpoint properties as server-owned symbolic values.</summary>
[AspireExport]
internal sealed class RpcEndpoint(RpcResource resource, string name) : ICapabilityLifetime
{
    public bool IsRevoked => resource.IsRevoked;

    /// <summary>Creates a host, port, hostAndPort or URL expression without resolving its allocation.</summary>
    [AspireExport]
    public DeclaredValue Property(string property)
    {
        var composition = resource.Owner.Composition;
        var endpoint = composition.Model.ReadResource(resource.Resource.Handle).Workload?.Plan.Endpoints
            .FirstOrDefault(endpoint => endpoint.Name == name)
            ?? throw new InvalidOperationException("The endpoint has not been declared.");

        return property == "url"
            ? composition.CreateLiteralValue($"{endpoint.UriScheme}://")
                .ConcatValue(composition.CreateEndpointValue(resource.Resource, name, "hostAndPort"))
            : composition.CreateEndpointValue(resource.Resource, name, property);
    }
}

/// <summary>Bounds remote editor authority to an active callback and resource generation.</summary>
internal abstract class RpcEditorScope(RpcResource resource) : ICapabilityLifetime
{
    private int _closed;
    protected RpcResource Resource { get; } = resource;
    public bool IsRevoked => Volatile.Read(ref _closed) != 0 || Resource.IsRevoked;
    internal void Close() => Interlocked.Exchange(ref _closed, 1);
}

/// <summary>Grants callback-scoped environment editing authority.</summary>
[AspireExport]
internal sealed class RpcEnvironmentEditor(RpcResource resource) : RpcEditorScope(resource)
{
    /// <summary>Assigns a literal or symbolic environment variable.</summary>
    [AspireExport]
    public void Set(string name, [AspireUnion(typeof(string), typeof(DeclaredValue), typeof(RpcEndpoint), typeof(RpcResource))] object value) =>
        Resource.WithEnvironment(name, value);
}

/// <summary>Grants callback-scoped argument editing authority.</summary>
[AspireExport]
internal sealed class RpcArgsEditor(RpcResource resource) : RpcEditorScope(resource)
{
    /// <summary>Appends a literal or symbolic argument.</summary>
    [AspireExport]
    public void Add([AspireUnion(typeof(string), typeof(DeclaredValue), typeof(RpcEndpoint), typeof(RpcResource))] object value) =>
        Resource.Resource.WithArgument(Resource.Value(value));
}
