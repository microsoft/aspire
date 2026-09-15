// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius;
using Aspire.Hosting.Radius.Publishing;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding and configuring Radius environment resources.
/// </summary>
public static partial class RadiusExtensions
{
    private const int KubernetesNamespaceMaxLength = 63;

    /// <summary>
    /// Adds a Radius compute environment to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the Radius environment resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{RadiusEnvironmentResource}"/>.</returns>
    /// <remarks>
    /// In <see cref="DistributedApplicationOperation.Run"/> mode this returns an
    /// unregistered builder so the environment does not surface as a resource in
    /// the dashboard and no pipeline steps are wired up — matching
    /// <c>AddKubernetesEnvironment</c> and <c>AddDockerComposeEnvironment</c>.
    /// All deployment-target wiring runs in Publish mode only.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<RadiusEnvironmentResource> AddRadiusEnvironment(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new RadiusEnvironmentResource(name);

        if (builder.ExecutionContext.IsRunMode)
        {
            // Return a builder that isn't added to the top-level application builder so it
            // doesn't surface as a resource. The Radius integration is publish/deploy-only
            // today; Run mode has nothing to wire up. The pipeline-step annotations on the
            // resource (registered in its constructor) are inert because the resource is
            // not in the application model — matches AddKubernetesEnvironment and
            // AddDockerComposeEnvironment.
            return builder.CreateResourceBuilder(resource);
        }

        return builder.AddResource(resource);
    }

    /// <summary>
    /// Sets the Kubernetes namespace for the Radius environment.
    /// </summary>
    /// <param name="builder">The Radius environment resource builder.</param>
    /// <param name="kubernetesNamespace">A valid RFC 1123 namespace name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{RadiusEnvironmentResource}"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when the namespace is not a valid RFC 1123 label.</exception>
    [AspireExport]
    public static IResourceBuilder<RadiusEnvironmentResource> WithNamespace(
        this IResourceBuilder<RadiusEnvironmentResource> builder,
        string kubernetesNamespace)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(kubernetesNamespace);

        if (kubernetesNamespace.Length > KubernetesNamespaceMaxLength || !DnsLabelPattern().IsMatch(kubernetesNamespace))
        {
            throw new ArgumentException(
                $"Kubernetes namespace '{kubernetesNamespace}' is invalid. " +
                "Must match RFC 1123: lowercase alphanumeric characters or hyphens, " +
                $"start and end with an alphanumeric character, and be at most {KubernetesNamespaceMaxLength} characters.",
                nameof(kubernetesNamespace));
        }

        builder.Resource.Namespace = kubernetesNamespace;
        return builder;
    }

    /// <summary>
    /// Registers a callback that can customize the generated Radius infrastructure before Bicep is emitted.
    /// </summary>
    /// <param name="builder">The Radius environment resource builder.</param>
    /// <param name="configure">The callback that mutates the generated infrastructure options.</param>
    /// <returns>The same <see cref="IResourceBuilder{RadiusEnvironmentResource}"/> for chaining.</returns>
    [Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "RadiusInfrastructureOptions customization callbacks are not ATS-compatible.")]
    public static IResourceBuilder<RadiusEnvironmentResource> ConfigureRadiusInfrastructure(
        this IResourceBuilder<RadiusEnvironmentResource> builder,
        Action<RadiusInfrastructureOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Resource.Annotations.Add(new RadiusInfrastructureConfigureAnnotation(configure));
        return builder;
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex DnsLabelPattern();

    /// <summary>
    /// Associates a pre-built container image reference with a project resource so the
    /// Aspire.Hosting.Radius publisher can emit a valid Radius container manifest for it.
    /// </summary>
    /// <param name="builder">The project resource builder.</param>
    /// <param name="image">
    /// A fully-qualified image reference in <c>[registry/]image[:tag]</c> form
    /// (for example <c>localhost:5001/apiservice:latest</c>). When the tag is omitted
    /// <c>latest</c> is used.
    /// </param>
    /// <returns>The same <see cref="IResourceBuilder{ProjectResource}"/> for chaining.</returns>
    /// <remarks>
    /// The Radius publisher does not yet build or push images for <see cref="ProjectResource"/>
    /// (tracked at https://github.com/microsoft/aspire/issues/16844). Until that lands,
    /// callers must build and push the image themselves (for example with
    /// <c>dotnet publish /t:PublishContainer</c>) and then call this method to attach
    /// the resulting registry reference. Without it, <c>aspire publish</c> against a Radius
    /// environment fails with a clear remediation message — and <c>aspire deploy</c> against
    /// Radius would otherwise land pods that hit <c>ImagePullBackOff</c> against the
    /// publisher's <c>&lt;name&gt;:latest</c> fallback.
    ///
    /// The reference is stored as a <see cref="ContainerImageAnnotation"/>, which is also
    /// what container resources use, so the publisher's existing
    /// <c>Registry</c>/<c>Image</c>/<c>Tag</c> assembly path applies uniformly.
    /// </remarks>
    [Experimental("ASPIRERADIUS057", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport]
    public static IResourceBuilder<ProjectResource> WithContainerImage(
        this IResourceBuilder<ProjectResource> builder,
        string image)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(image);

        var (registry, repository, tag) = ParseImageReference(image);

        // Replace any previous annotation so callers can override an earlier attachment;
        // matches the LastOrDefault() lookup behaviour the publisher uses.
        var existing = builder.Resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();
        if (existing is not null)
        {
            builder.Resource.Annotations.Remove(existing);
        }

        builder.Resource.Annotations.Add(new ContainerImageAnnotation
        {
            Registry = registry,
            Image = repository,
            Tag = tag,
        });

        return builder;
    }

    // Parse [registry/]image[:tag] into its three components. Examples:
    //   "redis"                       -> (null, "redis",            "latest")
    //   "redis:7"                     -> (null, "redis",            "7")
    //   "library/redis:7"             -> (null, "library/redis",    "7")
    //   "localhost:5001/api:latest"   -> ("localhost:5001", "api",  "latest")
    //   "ghcr.io/owner/repo:v1"       -> ("ghcr.io", "owner/repo",  "v1")
    //
    // A leading path segment is treated as a registry when it contains a '.' or ':' or
    // equals "localhost" — the same heuristic Docker/containerd use to disambiguate a
    // registry hostname from a Docker Hub user namespace. Digests (@sha256:...) are
    // intentionally not supported here; callers needing digest pinning can construct a
    // ContainerImageAnnotation directly.
    private static (string? Registry, string Image, string Tag) ParseImageReference(string reference)
    {
        var registry = (string?)null;
        var remainder = reference;

        var firstSlash = reference.IndexOf('/');
        if (firstSlash > 0)
        {
            var firstSegment = reference[..firstSlash];
            if (firstSegment == "localhost" || firstSegment.Contains('.') || firstSegment.Contains(':'))
            {
                registry = firstSegment;
                remainder = reference[(firstSlash + 1)..];
            }
        }

        // Tag separator: the last ':' that appears after the last '/' in the remainder
        // (so we don't mistake a port-in-registry for a tag, which is already split off above).
        var tag = "latest";
        var image = remainder;
        var lastSlash = remainder.LastIndexOf('/');
        var lastColon = remainder.LastIndexOf(':');
        if (lastColon > lastSlash)
        {
            tag = remainder[(lastColon + 1)..];
            image = remainder[..lastColon];
        }

        return (registry, image, tag);
    }
}
