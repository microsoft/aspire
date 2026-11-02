// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Client;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Native.Authoring;

/// <summary>Creates a synchronous authoring plan; creation never connects or performs RPC.</summary>
public static class DistributedApplication
{
    /// <summary>Creates a builder from framework/runtime configuration and ordinary application arguments.</summary>
    /// <param name="args">Framework selection and application configuration flags.</param>
    /// <returns>A synchronous, disconnected command recorder.</returns>
    public static DistributedApplicationBuilder CreateBuilder(string[] args) => CreateBuilder(args, new NativeBuilderOptions());
    /// <summary>Creates a command recorder using stable application identity and injected framework services.</summary>
    /// <param name="args">Framework selection and application configuration flags.</param>
    /// <param name="options">Application root/name, framework time and classified logging services.</param>
    /// <returns>A synchronous, disconnected command recorder.</returns>
    public static DistributedApplicationBuilder CreateBuilder(string[] args, NativeBuilderOptions options) =>
        new(NativeBuilderSettings.FromArgs(args, options));
}

/// <summary>Records typed composition commands. This is not a second runtime graph or credential evaluator.</summary>
public sealed partial class DistributedApplicationBuilder
{
    private readonly List<Func<NativeBuildContext, Task>> _declarations = [];
    private readonly List<Func<NativeBuildContext, Task>> _configuration = [];
    private readonly List<Resource> _resources = [];
    private readonly Dictionary<string, string> _providers = new(StringComparer.Ordinal);
    private bool _built;
    internal NativeBuilderSettings Settings { get; }
    /// <summary>Gets environment, supplied configuration and application arguments in increasing precedence.</summary>
    public IConfiguration Configuration => Settings.Configuration;
    /// <summary>Gets inert authored declarations for integration callback lookup, not native runtime state.</summary>
    public IReadOnlyList<Resource> Resources => _resources.AsReadOnly();
    internal IReadOnlyDictionary<string, string> Providers => _providers;
    internal DistributedApplicationBuilder(NativeBuilderSettings settings) => Settings = settings;

    /// <summary>Creates the same command recorder for a provider's delegated native composition.</summary>
    /// <returns>A disconnected integration-author builder.</returns>
    public static DistributedApplicationBuilder ForIntegration() => new(new());

    /// <summary>Adds an inert resource through the generic container primitive.</summary>
    /// <typeparam name="T">An integration-owned container resource type.</typeparam>
    /// <param name="resource">Inert authoring data.</param>
    /// <returns>The typed fluent container builder.</returns>
    public IResourceBuilder<T> AddContainer<T>(T resource) where T : ContainerResource
    {
        Add(resource);
        _declarations.Add(async context => context.Bind(resource,
            await context.Composition.AddContainerAsync(resource.Name, resource.Image, context.Token).ConfigureAwait(false)));

        return new ResourceBuilder<T>(this, resource);
    }

    /// <summary>Declares a generic container synchronously.</summary>
    /// <param name="name">The logical resource name.</param>
    /// <param name="image">The container image.</param>
    /// <returns>The typed fluent container builder.</returns>
    public IResourceBuilder<ContainerResource> AddContainer(string name, string image) => AddContainer(new ContainerResource(name, image));

    /// <summary>Declares an executable synchronously without starting a process.</summary>
    /// <param name="name">The logical resource name.</param>
    /// <param name="executablePath">The absolute executable path.</param>
    /// <param name="workingDirectory">The absolute working directory.</param>
    /// <returns>The typed fluent executable builder.</returns>
    public IResourceBuilder<ExecutableResource> AddExecutable(string name, string executablePath, string workingDirectory)
    {
        var resource = new ExecutableResource(name, executablePath, workingDirectory);
        Add(resource);
        _declarations.Add(async context => context.Bind(resource,
            await context.Composition.AddExecutableAsync(name, executablePath, workingDirectory, context.Token).ConfigureAwait(false)));

        return new ResourceBuilder<ExecutableResource>(this, resource);
    }

    /// <summary>Declares a parameter; absent secret values request a native-generated default credential.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">An explicit value, or null to request native generation for a secret.</param>
    /// <param name="secret">Whether to store the value in native secret storage.</param>
    /// <returns>A symbolic parameter builder.</returns>
    public IResourceBuilder<ParameterResource> AddParameter(string name, string? value = null, bool secret = true)
    {
        var resource = new ParameterResource(name, secret);
        Add(resource);
        _declarations.Insert(0, async context => context.BindParameter(resource, secret
            ? value is null ? await context.Composition.AddGeneratedSecretParameterAsync(name, context.Token).ConfigureAwait(false) :
                await context.Composition.AddSecretParameterAsync(name, value, context.Token).ConfigureAwait(false)
            : await context.Composition.CreateLiteralValueAsync(value ?? throw new ArgumentException("A nonsecret parameter requires a value."),
                context.Token).ConfigureAwait(false)));

        return new ResourceBuilder<ParameterResource>(this, resource);
    }

    /// <summary>Records a provider-backed resource using generated projection calls, never raw capability IDs.</summary>
    /// <typeparam name="T">The inert integration resource type.</typeparam>
    /// <param name="resource">Integration-owned authoring data.</param>
    /// <param name="packageName">The selected provider package identity.</param>
    /// <param name="declare">The generated projection adapter, invoked only during asynchronous materialization.</param>
    /// <returns>The typed integration resource builder.</returns>
    public IResourceBuilder<T> AddIntegrationResource<T>(T resource, string packageName,
        Func<NativeBuildContext, Task<(DeclaredResource Declaration, ReferenceExpression Connection)>> declare) where T : Resource
    {
        Add(resource);
        _providers.TryAdd(packageName, packageName);
        _declarations.Add(async context =>
        {
            var declared = await declare(context).ConfigureAwait(false);
            context.Bind(resource, declared.Declaration);
            context.BindConnection(resource, declared.Connection);
        });

        return new ResourceBuilder<T>(this, resource);
    }

    /// <summary>Imports an already delegated parameter at the generated provider boundary, without reading its value.</summary>
    /// <param name="name">The authoring parameter name.</param>
    /// <param name="value">Original delegated native symbolic authority.</param>
    /// <returns>An inert parameter builder.</returns>
    public IResourceBuilder<ParameterResource> ImportParameter(string name, DeclaredValue value)
    {
        var resource = new ParameterResource(name, true);
        Add(resource);
        _declarations.Insert(0, context =>
        {
            context.BindParameter(resource, value);
            return Task.CompletedTask;
        });

        return new ResourceBuilder<ParameterResource>(this, resource);
    }

    private void Add(Resource resource)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Name);
        if (_resources.Any(existing => string.Equals(existing.Name, resource.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Duplicate authored resource name.");
        }
        _resources.Add(resource);
    }
    internal void Record(Func<NativeBuildContext, Task> command)
    {
        EnsureMutable();
        _configuration.Add(command);
    }
    internal void EnsureMutable()
    {
        if (_built)
        {
            throw new InvalidOperationException("The authoring plan is sealed.");
        }
    }
    internal string VolumeName(Resource resource) => $"{Settings.VolumePrefix}-{resource.Name}-data";
    /// <summary>Gets the stable default name of a retained data volume, without creating it.</summary>
    /// <param name="resource">The authored volume owner.</param>
    /// <returns>The configured prefix, resource name and data suffix.</returns>
    public string GetDefaultDataVolumeName(Resource resource) => VolumeName(resource);
    /// <summary>Seals authoring commands without blocking on a network operation.</summary>
    /// <returns>A disconnected application runtime adapter.</returns>
    public NativeApplication Build()
    {
        EnsureMutable();
        _built = true;

        return new(this);
    }
    /// <summary>Materializes provider-side composition asynchronously through the same generated native projection.</summary>
    /// <param name="composition">The original delegated native composition.</param>
    /// <param name="cancellationToken">Cancels caller waits without replaying accepted native side effects.</param>
    /// <returns>The generated-handle adapter for this materialization.</returns>
    public async Task<NativeBuildContext> MaterializeAsync(Composition composition, CancellationToken cancellationToken)
    {
        _built = true;
        var context = new NativeBuildContext(composition, cancellationToken);
        foreach (var declaration in _declarations)
        {
            await declaration(context).ConfigureAwait(false);
        }
        foreach (var command in _configuration)
        {
            await command(context).ConfigureAwait(false);
        }

        return context;
    }
}

internal sealed class ResourceBuilder<T>(DistributedApplicationBuilder builder, T resource) : IResourceBuilder<T> where T : Resource
{
    public T Resource { get; } = resource;
    public DistributedApplicationBuilder ApplicationBuilder { get; } = builder;
}

/// <summary>Adapts typed command records to generated native handles at asynchronous transport boundaries.</summary>
/// <param name="composition">Original native composition authority.</param>
/// <param name="token">Materialization cancellation.</param>
public sealed class NativeBuildContext(Composition composition, CancellationToken token)
{
    private readonly Dictionary<Resource, DeclaredResource> _declarations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ParameterResource, DeclaredValue> _parameters = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Resource, ReferenceExpression> _connections = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ReferenceExpression, Task<DeclaredValue>> _values = new(ReferenceEqualityComparer.Instance);
    /// <summary>Gets the generated composition projection for framework/provider adapters.</summary>
    public Composition Composition { get; } = composition;
    /// <summary>Gets materialization cancellation.</summary>
    public CancellationToken Token { get; } = token;
    /// <summary>Gets a generated declaration projection at the adapter boundary.</summary>
    /// <param name="resource">The inert authored resource.</param>
    /// <returns>Its original native declaration.</returns>
    public DeclaredResource Declaration(Resource resource) => _declarations[resource];
    internal void Bind(Resource resource, DeclaredResource declaration) => _declarations.Add(resource, declaration);
    internal void BindParameter(ParameterResource resource, DeclaredValue value) => _parameters.Add(resource, value);
    internal void BindConnection(Resource resource, ReferenceExpression connection) => _connections.Add(resource, connection);
    internal Task<DeclaredValue> Parameter(ParameterResource resource) => Task.FromResult(_parameters[resource]);
    /// <summary>Materializes symbolic syntax as native-owned handles, never resolved strings.</summary>
    /// <param name="expression">Recorded symbolic syntax.</param>
    /// <returns>Native value authority, not a payload.</returns>
    public Task<DeclaredValue> Value(ReferenceExpression expression)
    {
        if (!_values.TryGetValue(expression, out var value))
        {
            value = expression.Materialize(this);
            _values.Add(expression, value);
        }

        return value;
    }
    internal ReferenceExpression Connection(Resource resource) =>
        resource is IResourceWithConnectionString local ? local.ConnectionStringExpression : _connections[resource];
    /// <summary>Creates a reference to an already generated native value at a transport adapter boundary.</summary>
    /// <param name="value">Original delegated symbolic authority.</param>
    /// <returns>Recorded native symbolic syntax.</returns>
    public static ReferenceExpression Reference(DeclaredValue value) => new(_ => Task.FromResult(value));
    /// <summary>Records a connection supplied by native/provider materialization rather than authoring-time resolution.</summary>
    /// <param name="resource">The inert connection owner.</param>
    /// <returns>Deferred native connection syntax.</returns>
    public static ReferenceExpression ConnectionReference(Resource resource) => new(context => context.Value(context._connections[resource]));
}
