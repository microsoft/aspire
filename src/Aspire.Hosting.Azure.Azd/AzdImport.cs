// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Azd;

/// <summary>
/// Represents the result of importing an azd project into an Aspire application model.
/// </summary>
/// <remarks>
/// An <see cref="AzdImport"/> is returned from
/// <see cref="AzdProjectBuilderExtensions.AddAzdProject(IDistributedApplicationBuilder, string?, System.Action{AzdImportOptions}?)"/>.
/// It exposes the parsed <see cref="AzdProject"/>, the resolved <see cref="Environment"/>, the Aspire
/// resource builders created for each azd service and resource, and any <see cref="Diagnostics"/>
/// produced. Use <see cref="GetService(string)"/> and <see cref="GetResource(string)"/> to grab a
/// reference to an imported service or resource and customize it like any other Aspire resource, or the
/// strongly-typed <see cref="GetService{T}(string)"/> and <see cref="GetResource{T}(string)"/> overloads
/// when you need the concrete resource type to call type-specific extension methods.
/// </remarks>
[Experimental("ASPIREAZUREAZD001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
// Exposed to polyglot (e.g. TypeScript) app hosts so the loosely-typed GetService/GetResource accessors
// can be called over the Aspire Type System. The generic GetService{T}/GetResource{T} overloads are not
// exported because ATS does not support generic methods; polyglot hosts use the loose accessors, which
// hand back a base resource handle that composes with name-based operations such as WaitFor.
[AspireExport(ExposeMethods = true)]
public sealed class AzdImport
{
    private readonly IDistributedApplicationBuilder _builder;
    private readonly Dictionary<string, IResourceBuilder<IResource>> _services = [];
    private readonly Dictionary<string, IResourceBuilder<IResource>> _resources = [];

    // Names requested through GetService/GetResource that did not match an imported service or resource.
    // The failure is intentionally deferred (see Resolve): the app host keeps a usable reference in hand
    // and the application only fails later, when the deferred BeforeStartEvent validation runs.
    private readonly List<UnresolvedReference> _unresolved = [];
    private int _placeholderCount;
    private bool _deferredValidationRegistered;

    internal AzdImport(IDistributedApplicationBuilder builder, AzdProject project, string projectDirectory, AzdEnvironment? environment)
    {
        _builder = builder;
        Project = project;
        ProjectDirectory = projectDirectory;
        Environment = environment;
    }

    /// <summary>
    /// Gets the parsed azd project model.
    /// </summary>
    public AzdProject Project { get; }

    /// <summary>
    /// Gets the absolute path of the directory that contains the imported <c>azure.yaml</c>.
    /// </summary>
    public string ProjectDirectory { get; }

    /// <summary>
    /// Gets the azd environment that was loaded from the project's <c>.azure</c> directory, if any.
    /// </summary>
    public AzdEnvironment? Environment { get; }

    /// <summary>
    /// Gets the diagnostics produced during the import.
    /// </summary>
    public AzdImportDiagnostics Diagnostics { get; } = new();

    /// <summary>
    /// Gets the Aspire resource builders created for each azd <c>services</c> entry, keyed by service name.
    /// </summary>
    public IReadOnlyDictionary<string, IResourceBuilder<IResource>> Services => _services;

    /// <summary>
    /// Gets the Aspire resource builders created for each azd <c>resources</c> entry, keyed by resource name.
    /// </summary>
    public IReadOnlyDictionary<string, IResourceBuilder<IResource>> Resources => _resources;

    /// <summary>
    /// Gets the directory that holds the project's existing infrastructure-as-code, when present and preserved.
    /// </summary>
    /// <value>
    /// The absolute path to the azd <c>infra</c> directory, or <see langword="null"/> when the project
    /// has no discoverable infrastructure folder. The importer does not regenerate these files; they
    /// remain available for the existing deployment workflow.
    /// </value>
    public string? InfraPath { get; internal set; }

    /// <summary>
    /// Gets a reference to the Aspire resource created for the named azd <c>services</c> entry.
    /// </summary>
    /// <param name="name">The service name as it appears under <c>services:</c> in <c>azure.yaml</c>.</param>
    /// <returns>The resource builder for the imported service.</returns>
    /// <remarks>
    /// The reference is always returned so app host authoring code reads naturally
    /// (for example <c>azd.GetService("web").WithReplicas(2)</c>). If <paramref name="name"/> does not
    /// match an imported service the failure is deferred: the returned reference is a placeholder and the
    /// application fails later — when it is run, published, or deployed — with a diagnostic that names the
    /// missing service, rather than throwing at the call site.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    public IResourceBuilder<IResource> GetService(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return Resolve(name, _services, AzdReferenceKind.Service);
    }

    /// <summary>
    /// Gets a reference to the Aspire resource created for the named azd <c>resources</c> entry.
    /// </summary>
    /// <param name="name">The resource name as it appears under <c>resources:</c> in <c>azure.yaml</c>.</param>
    /// <returns>The resource builder for the imported resource.</returns>
    /// <remarks>
    /// The reference is always returned so app host authoring code reads naturally
    /// (for example <c>azd.GetResource("cache").WithReplicas(2)</c>). If <paramref name="name"/> does not
    /// match an imported resource the failure is deferred: the returned reference is a placeholder and the
    /// application fails later — when it is run, published, or deployed — with a diagnostic that names the
    /// missing resource, rather than throwing at the call site.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    public IResourceBuilder<IResource> GetResource(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return Resolve(name, _resources, AzdReferenceKind.Resource);
    }

    /// <summary>
    /// Gets a strongly-typed reference to the Aspire resource created for the named azd <c>services</c> entry.
    /// </summary>
    /// <typeparam name="T">The expected resource type, for example <c>ProjectResource</c>.</typeparam>
    /// <param name="name">The service name as it appears under <c>services:</c> in <c>azure.yaml</c>.</param>
    /// <returns>A strongly-typed resource builder for the imported service.</returns>
    /// <remarks>
    /// The import keeps every service in a loosely-typed model (<see cref="IResourceBuilder{IResource}"/>).
    /// This overload re-wraps the underlying resource with <c>CreateResourceBuilder</c> so it composes with
    /// the strongly-typed extension methods for <typeparamref name="T"/>. The builder it returns mutates the
    /// same underlying resource as the one in <see cref="Services"/>. Because a typed placeholder cannot be
    /// synthesized, an unknown name or a type mismatch is reported immediately rather than deferred.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// No service named <paramref name="name"/> was imported, or it is not a <typeparamref name="T"/>.
    /// </exception>
    [AspireExportIgnore(Reason = "ATS does not support generic methods; polyglot hosts use the non-generic GetService overload.")]
    public IResourceBuilder<T> GetService<T>(string name) where T : IResource
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return ResolveTyped<T>(name, _services, AzdReferenceKind.Service);
    }

    /// <summary>
    /// Gets a strongly-typed reference to the Aspire resource created for the named azd <c>resources</c> entry.
    /// </summary>
    /// <typeparam name="T">The expected resource type, for example <c>AzureKeyVaultResource</c>.</typeparam>
    /// <param name="name">The resource name as it appears under <c>resources:</c> in <c>azure.yaml</c>.</param>
    /// <returns>A strongly-typed resource builder for the imported resource.</returns>
    /// <remarks>
    /// The import keeps every resource in a loosely-typed model (<see cref="IResourceBuilder{IResource}"/>).
    /// This overload re-wraps the underlying resource with <c>CreateResourceBuilder</c> so it composes with
    /// the strongly-typed extension methods for <typeparamref name="T"/> (for example
    /// <c>azd.GetResource&lt;AzureCosmosDBResource&gt;("orders").AddDatabase(...)</c>). The builder it returns
    /// mutates the same underlying resource as the one in <see cref="Resources"/>. Because a typed placeholder
    /// cannot be synthesized, an unknown name or a type mismatch is reported immediately rather than deferred.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// No resource named <paramref name="name"/> was imported, or it is not a <typeparamref name="T"/>.
    /// </exception>
    [AspireExportIgnore(Reason = "ATS does not support generic methods; polyglot hosts use the non-generic GetResource overload.")]
    public IResourceBuilder<T> GetResource<T>(string name) where T : IResource
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return ResolveTyped<T>(name, _resources, AzdReferenceKind.Resource);
    }

    private IResourceBuilder<IResource> Resolve(string name, Dictionary<string, IResourceBuilder<IResource>> source, AzdReferenceKind kind)
    {
        if (source.TryGetValue(name, out var builder))
        {
            return builder;
        }

        // Defer the failure instead of throwing here. We hand back a placeholder reference so the caller's
        // fluent chain keeps compiling and reading naturally, and register a one-time validation that runs
        // before the app starts (BeforeStartEvent fires for both `aspire run` and `aspire publish`/`deploy`)
        // to report every bad reference together. The placeholder is created with CreateResourceBuilder, so
        // it is NOT added to the application model; the deferred validation is the only thing that fails,
        // which means an unused bad reference still surfaces rather than silently passing.
        EnsureDeferredValidationRegistered();
        _unresolved.Add(new UnresolvedReference(name, kind));

        var placeholderName = $"azd-unresolved-{++_placeholderCount}";
        return _builder.CreateResourceBuilder<IResource>(new UnresolvedAzdReferenceResource(placeholderName));
    }

    private IResourceBuilder<T> ResolveTyped<T>(string name, Dictionary<string, IResourceBuilder<IResource>> source, AzdReferenceKind kind)
        where T : IResource
    {
        if (source.TryGetValue(name, out var existing))
        {
            if (existing.Resource is T typed)
            {
                // The model holds IResourceBuilder<IResource> (the concrete builder upcast on import), and
                // IResourceBuilder<T> is invariant, so the stored builder cannot be cast to the typed builder.
                // Re-wrap the underlying resource so the caller gets a typed builder that composes with the
                // type-specific extension methods while still mutating the same underlying resource.
                return _builder.CreateResourceBuilder(typed);
            }

            // The name resolved but to the wrong type. A typed placeholder cannot be synthesized for an
            // arbitrary T, so unlike the loosely-typed accessors this cannot be deferred; report it now.
            throw new InvalidOperationException(
                $"The azd {DescribeKind(kind)} '{name}' imported from '{ProjectDirectory}' is '{existing.Resource.GetType().Name}', " +
                $"not '{typeof(T).Name}'. Use {Accessor(kind)}<{existing.Resource.GetType().Name}>(\"{name}\") or the loosely-typed {Accessor(kind)}(\"{name}\").");
        }

        // Unknown name: a typed placeholder cannot be synthesized either, so this is reported immediately.
        // The loosely-typed GetService/GetResource overloads are the ones that defer the failure to startup.
        throw new InvalidOperationException(
            $"The azd project imported from '{ProjectDirectory}': {DescribeReference(name, kind)} Check the names against azure.yaml.");
    }

    /// <summary>
    /// Resolves a reference to either an imported service or resource. Used internally to wire azd
    /// <c>uses</c> edges, where a dependency may target either section of <c>azure.yaml</c>.
    /// </summary>
    internal bool TryResolveReference(string name, [NotNullWhen(true)] out IResourceBuilder<IResource>? builder)
        => _services.TryGetValue(name, out builder) || _resources.TryGetValue(name, out builder);

    internal void AddService(string name, IResourceBuilder<IResource> builder) => _services[name] = builder;

    internal void AddResource(string name, IResourceBuilder<IResource> builder) => _resources[name] = builder;

    private void EnsureDeferredValidationRegistered()
    {
        if (_deferredValidationRegistered)
        {
            return;
        }

        _deferredValidationRegistered = true;

        // BeforeStartEvent is the earliest hook that runs for both `aspire run` and `aspire publish`/`deploy`
        // (see DistributedApplication.StartAsync), so it surfaces a bad reference regardless of how the app
        // host is invoked. Returning a faulted task guarantees the exception propagates out of PublishAsync.
        _builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
            _unresolved.Count == 0
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException(BuildUnresolvedReferenceMessage())));
    }

    private string BuildUnresolvedReferenceMessage()
    {
        var message = new StringBuilder();
        message.Append("The azd project imported from '")
               .Append(ProjectDirectory)
               .Append("' could not resolve the following reference(s) requested from the app host:");

        foreach (var reference in _unresolved)
        {
            message.AppendLine();
            message.Append(" - ").Append(DescribeReference(reference.Name, reference.Kind));
        }

        message.AppendLine();
        message.Append("Check the names against azure.yaml.");
        return message.ToString();
    }

    // Builds a single-reference diagnostic that points the caller at the right accessor, e.g.
    //   GetService("web") — no such service. Imported services: api, legacy.
    //   GetResource("web") — 'web' is a service, not a resource. Use GetService("web").
    private string DescribeReference(string name, AzdReferenceKind kind)
    {
        var description = new StringBuilder();
        description.Append(Accessor(kind)).Append("(\"").Append(name).Append("\") ");

        // If the requested name exists under the other azure.yaml section, point at the right accessor;
        // mixing up services and resources is the most likely mistake.
        var otherSection = kind == AzdReferenceKind.Service ? _resources : _services;
        if (otherSection.ContainsKey(name))
        {
            var otherKind = kind == AzdReferenceKind.Service ? AzdReferenceKind.Resource : AzdReferenceKind.Service;
            description.Append("— '").Append(name).Append("' is a ").Append(DescribeKind(otherKind))
                       .Append(", not a ").Append(DescribeKind(kind))
                       .Append(". Use ").Append(Accessor(otherKind)).Append("(\"").Append(name).Append("\").");
        }
        else
        {
            var available = kind == AzdReferenceKind.Service ? _services : _resources;
            description.Append("— no such ").Append(DescribeKind(kind))
                       .Append(". Imported ").Append(kind == AzdReferenceKind.Service ? "services" : "resources").Append(": ")
                       .Append(available.Count == 0 ? "(none)" : string.Join(", ", available.Keys.OrderBy(static k => k, StringComparer.Ordinal)))
                       .Append('.');
        }

        return description.ToString();
    }

    private static string Accessor(AzdReferenceKind kind) => kind == AzdReferenceKind.Service ? "GetService" : "GetResource";

    private static string DescribeKind(AzdReferenceKind kind) => kind == AzdReferenceKind.Service ? "service" : "resource";

    private enum AzdReferenceKind
    {
        Service,
        Resource
    }

    private readonly record struct UnresolvedReference(string Name, AzdReferenceKind Kind);

    // A stand-in for a service/resource name that did not match the import. It is never added to the
    // application model; it exists only to satisfy the IResourceBuilder<IResource> return type while the
    // real failure is reported by the deferred BeforeStartEvent validation.
    private sealed class UnresolvedAzdReferenceResource(string name) : Resource(name);
}
