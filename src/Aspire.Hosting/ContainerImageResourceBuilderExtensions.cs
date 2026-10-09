// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREDOCKERFILEBUILDER001

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for modeling source image artifacts and registry-scoped image destinations.
/// </summary>
public static class ContainerImageResourceBuilderExtensions
{
    /// <summary>
    /// Adds a standalone image artifact whose source is configured separately.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The source artifact resource name.</param>
    /// <returns>The image artifact builder.</returns>
    /// <remarks>
    /// Configure an existing registry image with <c>WithImageSource</c>, or a build description with
    /// <c>WithDockerfile</c> or <c>WithDockerfileBuilder</c>. Sources are independent
    /// of destinations and can be shared across registries. This experimental API registers
    /// artifacts in publish mode only; local image preparation and registry emulation are not implemented.
    /// Build prepares Dockerfile artifacts without publishing them. Deployment builds each source once and
    /// publishes its complete image to every selected destination. Dockerfile artifacts require Docker's
    /// containerd image store to retain all platforms and attestations; Podman artifact publication is not implemented.
    /// Deployment resolves each associated remote source once and publishes its complete referenced
    /// content to each destination using Docker with Buildx. Separate OCI referrers are not copied.
    /// </remarks>
    /// <example>
    /// <code>
    /// var source = builder.AddContainerImage("tools").WithImageSource("ghcr.io/example/tools:v1");
    /// var destination = registry.AddImage("published-tools", source);
    /// app.WithEnvironment("IMAGE_NAME", destination);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder or name is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is empty or invalid.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport]
    public static IResourceBuilder<ContainerImageResource> AddContainerImage(
        this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var resource = new ContainerImageResource(name);
        var resourceBuilder = builder.ExecutionContext.IsRunMode
            ? builder.CreateResourceBuilder(resource)
            : builder.AddResource(resource);
        ContainerImagePublishing.Configure(builder, resource);

        return resourceBuilder.WithManifestPublishingCallback(context => WriteSourceManifestAsync(context, resource));
    }

    /// <summary>
    /// Configures an existing registry image as the source of an image artifact.
    /// </summary>
    /// <param name="builder">The source artifact builder.</param>
    /// <param name="image">The image reference, including an optional tag, digest, or both.</param>
    /// <returns>The original source artifact builder.</returns>
    /// <remarks>
    /// Source configuration is last-wins. This method records deferred preparation intent;
    /// it does not pull or push an image. A source tag must be pinned to content during preparation,
    /// before publication to any destinations. Reconfiguration invalidates previously recorded publication digests.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The builder or image is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source image reference is invalid.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("withContainerImageSource", MethodName = "withImageSource")]
    public static IResourceBuilder<ContainerImageResource> WithImageSource(
        this IResourceBuilder<ContainerImageResource> builder, string image)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var source = ContainerImageName.ParseSource(image);
        RemoveBuildSource(builder.Resource);

        return builder.WithAnnotation(new ContainerImageSourceAnnotation(source), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Configures a Dockerfile build as the source of an image artifact.
    /// </summary>
    /// <param name="builder">The image artifact builder.</param>
    /// <param name="contextPath">The build context path, relative to the AppHost directory unless absolute.</param>
    /// <param name="dockerfilePath">The Dockerfile path, relative to the build context unless absolute. Defaults to <c>Dockerfile</c>.</param>
    /// <param name="stage">The optional target stage in a multi-stage Dockerfile.</param>
    /// <returns>The original image artifact builder.</returns>
    /// <remarks>
    /// Source configuration is last-wins. Replacing a source discards its build arguments, secrets, and generated
    /// Dockerfile callbacks, and invalidates publication evidence. Explicit container build options are retained.
    /// The resource remains an artifact, not a runnable container. This records a build description without building
    /// or publishing it during model construction. Build and deployment use Docker with its containerd image store;
    /// unsupported stores fail explicitly rather than discarding platforms or attestations. Local run-mode preparation is not implemented.
    /// </remarks>
    /// <example>
    /// <code>
    /// var source = builder.AddContainerImage("tools").WithDockerfile("./tools");
    /// var destination = registry.AddImage("published-tools", source);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder or context path is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The context path is empty or a path is invalid.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("withContainerImageDockerfile", MethodName = "withDockerfile")]
    public static IResourceBuilder<ContainerImageResource> WithDockerfile(
        this IResourceBuilder<ContainerImageResource> builder,
        string contextPath,
        string? dockerfilePath = null,
        string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(contextPath);
        var context = Path.GetFullPath(contextPath, builder.ApplicationBuilder.AppHostDirectory);
        var dockerfile = Path.GetFullPath(dockerfilePath ?? "Dockerfile", context);
        var annotation = new DockerfileBuildAnnotation(context, dockerfile, stage);

        return SetBuildSource(builder, annotation);
    }

    /// <summary>
    /// Configures an image artifact's Dockerfile using an asynchronous builder callback.
    /// </summary>
    /// <param name="builder">The image artifact builder.</param>
    /// <param name="contextPath">The build context path, relative to the AppHost directory unless absolute.</param>
    /// <param name="callback">The callback that constructs the Dockerfile.</param>
    /// <param name="stage">The optional target stage in a multi-stage Dockerfile.</param>
    /// <returns>The original image artifact builder.</returns>
    /// <remarks>
    /// Repeated builder calls compose callbacks in registration order; the first call establishes the context and stage.
    /// Generation is deferred and uses UTF-8 without a BOM and LF line endings. Adding a callback invalidates generated
    /// content and publication evidence, while retaining build arguments and secrets. Selecting a different source
    /// discards the callbacks. Each pipeline execution rematerializes the callbacks and builds the source once.
    /// Publication requires Docker's containerd image store. Local run-mode preparation is not implemented.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The builder, context path, or callback is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The context path is empty or invalid.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("withContainerImageDockerfileBuilder", MethodName = "withDockerfileBuilder")]
    public static IResourceBuilder<ContainerImageResource> WithDockerfileBuilder(
        this IResourceBuilder<ContainerImageResource> builder,
        string contextPath,
        Func<DockerfileBuilderCallbackContext, Task> callback,
        string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(contextPath);
        ArgumentNullException.ThrowIfNull(callback);
        var context = Path.GetFullPath(contextPath, builder.ApplicationBuilder.AppHostDirectory);
        var callbacks = new DockerfileBuilderCallbackAnnotation();
        DockerfileBuildAnnotation annotation;
        if (builder.Resource.TryGetLastAnnotation<DockerfileBuilderCallbackAnnotation>(out var previous))
        {
            var original = builder.Resource.GetSource().Dockerfile
                ?? throw new InvalidOperationException($"Image artifact '{builder.Resource.Name}' has Dockerfile callbacks without a Dockerfile source.");
            foreach (var existing in previous.Callbacks)
            {
                callbacks.AddCallback(existing);
            }
            // A fresh annotation invalidates materialized output when callbacks are appended.
            annotation = DockerfileHelper.CloneBuildAnnotation(original);
        }
        else
        {
            var dockerfile = builder.ApplicationBuilder.FileSystemService.TempDirectory.CreateTempFile("Dockerfile").Path;
            annotation = new DockerfileBuildAnnotation(context, dockerfile, stage)
            {
                DockerfileFactory = DockerfileHelper.CreateDockerfileFromBuilderAsync
            };
        }
        callbacks.AddCallback(callback);
        SetBuildSource(builder, annotation);

        return builder.WithAnnotation(callbacks, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Configures an image artifact's Dockerfile using a synchronous builder callback.
    /// </summary>
    /// <param name="builder">The image artifact builder.</param>
    /// <param name="contextPath">The build context path, relative to the AppHost directory unless absolute.</param>
    /// <param name="callback">The callback that constructs the Dockerfile.</param>
    /// <param name="stage">The optional target stage in a multi-stage Dockerfile.</param>
    /// <returns>The original image artifact builder.</returns>
    /// <remarks>Uses the same deferred, composing behavior as the asynchronous overload.</remarks>
    /// <exception cref="ArgumentNullException">The builder, context path, or callback is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The context path is empty or invalid.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the asynchronous withDockerfileBuilder callback.")]
    public static IResourceBuilder<ContainerImageResource> WithDockerfileBuilder(
        this IResourceBuilder<ContainerImageResource> builder,
        string contextPath,
        Action<DockerfileBuilderCallbackContext> callback,
        string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithDockerfileBuilder(contextPath, context =>
        {
            callback(context);
            return Task.CompletedTask;
        }, stage);
    }

    /// <summary>
    /// Adds an argument to an image artifact's Dockerfile build.
    /// </summary>
    /// <param name="builder">The image artifact builder.</param>
    /// <param name="name">The build argument name.</param>
    /// <param name="value">The argument value, or <see langword="null"/> to use the build process's environment.</param>
    /// <returns>The original image artifact builder.</returns>
    /// <remarks>Configure a Dockerfile source first. Secret parameters must use <c>WithBuildSecret</c>.</remarks>
    /// <exception cref="ArgumentNullException">The builder or name is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    /// <exception cref="InvalidOperationException">There is no Dockerfile source or the value is a secret parameter.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the string-or-parameter withBuildArg dispatcher.")]
    public static IResourceBuilder<ContainerImageResource> WithBuildArg(
        this IResourceBuilder<ContainerImageResource> builder, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (value is ParameterResource { Secret: true } parameter)
        {
            throw new InvalidOperationException(
                $"Cannot add secret parameter '{parameter.Name}' as build argument '{name}' while configuring resource '{builder.Resource.Name}'. Use WithBuildSecret instead.");
        }
        UpdateBuildSource(builder.Resource, nameof(WithBuildArg), annotation => annotation.BuildArguments[name] = value);

        return builder;
    }

    /// <summary>
    /// Adds a parameter-backed argument to an image artifact's Dockerfile build.
    /// </summary>
    /// <param name="builder">The image artifact builder.</param>
    /// <param name="name">The build argument name.</param>
    /// <param name="value">A non-secret parameter builder belonging to the same application.</param>
    /// <returns>The original image artifact builder.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is empty or the builders belong to different applications.</exception>
    /// <exception cref="InvalidOperationException">There is no Dockerfile source or the parameter is secret.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the string-or-parameter withBuildArg dispatcher.")]
    public static IResourceBuilder<ContainerImageResource> WithBuildArg(
        this IResourceBuilder<ContainerImageResource> builder, string name, IResourceBuilder<ParameterResource> value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(value);
        ValidateApplication(builder.ApplicationBuilder, value.ApplicationBuilder);

        return builder.WithBuildArg(name, value.Resource);
    }

    /// <summary>
    /// Adds an argument to an image artifact's Dockerfile build.
    /// </summary>
    /// <param name="builder">The image artifact builder.</param>
    /// <param name="name">The build argument name.</param>
    /// <param name="value">A string or non-secret parameter builder.</param>
    /// <returns>The original image artifact builder.</returns>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(MethodName = "withBuildArg")]
    internal static IResourceBuilder<ContainerImageResource> WithContainerImageBuildArg(
        this IResourceBuilder<ContainerImageResource> builder,
        string name,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            string text => builder.WithBuildArg(name, (object)text),
            IResourceBuilder<ParameterResource> parameter => builder.WithBuildArg(name, parameter),
            _ => throw new ArgumentException("Expected a string or parameter builder.", nameof(value))
        };
    }

    /// <summary>
    /// Adds a parameter-backed secret to an image artifact's Dockerfile build.
    /// </summary>
    /// <param name="builder">The image artifact builder.</param>
    /// <param name="name">The build secret name.</param>
    /// <param name="value">The parameter builder belonging to the same application.</param>
    /// <returns>The original image artifact builder.</returns>
    /// <remarks>The value remains a parameter reference in manifests and is passed as a build secret, not an argument.</remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is empty or the builders belong to different applications.</exception>
    /// <exception cref="InvalidOperationException">There is no Dockerfile source.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("withContainerImageBuildSecret", MethodName = "withBuildSecret")]
    public static IResourceBuilder<ContainerImageResource> WithBuildSecret(
        this IResourceBuilder<ContainerImageResource> builder, string name, IResourceBuilder<ParameterResource> value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        ValidateApplication(builder.ApplicationBuilder, value.ApplicationBuilder);
        UpdateBuildSource(builder.Resource, nameof(WithBuildSecret), annotation => annotation.BuildSecrets[name] = value.Resource);

        return builder;
    }

    private static IResourceBuilder<ContainerImageResource> SetBuildSource(
        IResourceBuilder<ContainerImageResource> builder, DockerfileBuildAnnotation annotation)
    {
        annotation.ImageName ??= ImageNameGenerator.GenerateImageName(builder);
        annotation.ImageTag ??= ImageNameGenerator.GenerateImageTag(builder);
        var defaults = DockerfileHelper.CreateDefaultBuildOptions();
        RemoveBuildSource(builder.Resource);
        // Keep defaults before user callbacks even when the source is reconfigured.
        builder.Resource.Annotations.Insert(0, defaults);
        builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);

        return builder.WithAnnotation(new ContainerImageSourceAnnotation(annotation, defaults), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Configures an image artifact's container build options using an asynchronous callback.
    /// </summary>
    /// <param name="builder">The image artifact builder.</param>
    /// <param name="callback">The callback that configures build options.</param>
    /// <returns>The original image artifact builder.</returns>
    /// <remarks>
    /// Explicit callbacks run after source defaults and survive source replacement.
    /// Use <c>ContainerTargetPlatform.AllLinux</c> to select both Linux AMD64 and ARM64.
    /// Docker archive output is supported; OCI archive output is not implemented for Dockerfile image artifacts.
    /// Adding options invalidates materialized Dockerfile content and publication evidence.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The builder or callback is <see langword="null"/>.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("withContainerImageBuildOptions", MethodName = "withContainerBuildOptions")]
    public static IResourceBuilder<ContainerImageResource> WithContainerBuildOptions(
        this IResourceBuilder<ContainerImageResource> builder,
        Func<ContainerBuildOptionsCallbackContext, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);
        if (builder.Resource.TryGetLastAnnotation<ContainerImageSourceAnnotation>(out var source) && source.Dockerfile is not null)
        {
            var active = builder.Resource.GetSource();
            var defaults = active.BuildOptions
                ?? throw new InvalidOperationException($"Image artifact '{builder.Resource.Name}' has no Dockerfile build options.");
            var annotation = DockerfileHelper.CloneBuildAnnotation(source.Dockerfile);
            builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace);
            builder.WithAnnotation(new ContainerImageSourceAnnotation(annotation, defaults), ResourceAnnotationMutationBehavior.Replace);
        }

        return builder.WithAnnotation(new ContainerBuildOptionsCallbackAnnotation(callback), ResourceAnnotationMutationBehavior.Append);
    }

    /// <summary>
    /// Configures an image artifact's container build options using a synchronous callback.
    /// </summary>
    /// <param name="builder">The image artifact builder.</param>
    /// <param name="callback">The callback that configures build options.</param>
    /// <returns>The original image artifact builder.</returns>
    /// <remarks>Uses the same ordering and invalidation behavior as the asynchronous overload.</remarks>
    /// <exception cref="ArgumentNullException">The builder or callback is <see langword="null"/>.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the asynchronous withContainerBuildOptions callback.")]
    public static IResourceBuilder<ContainerImageResource> WithContainerBuildOptions(
        this IResourceBuilder<ContainerImageResource> builder,
        Action<ContainerBuildOptionsCallbackContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithContainerBuildOptions(context =>
        {
            callback(context);
            return Task.CompletedTask;
        });
    }

    private static void RemoveBuildSource(ContainerImageResource resource)
    {
        if (resource.TryGetLastAnnotation<ContainerImageSourceAnnotation>(out var source) && source.BuildOptions is { } defaults)
        {
            resource.Annotations.Remove(defaults);
        }
        foreach (var annotation in resource.Annotations.Where(annotation =>
            annotation is DockerfileBuildAnnotation or DockerfileBuilderCallbackAnnotation).ToArray())
        {
            resource.Annotations.Remove(annotation);
        }
    }

    private static void UpdateBuildSource(
        ContainerImageResource resource, string methodName, Action<DockerfileBuildAnnotation> update)
    {
        var annotation = DockerfileHelper.GetBuildAnnotation(resource, methodName);
        var source = resource.GetSource();
        var defaults = source.BuildOptions
            ?? throw new InvalidOperationException($"Image artifact '{resource.Name}' has no Dockerfile build options.");
        var replacement = DockerfileHelper.CloneBuildAnnotation(annotation);
        update(replacement);
        resource.Annotations.Remove(annotation);
        resource.Annotations.Add(replacement);
        resource.Annotations.Remove(source);
        resource.Annotations.Add(new ContainerImageSourceAnnotation(replacement, defaults));
    }

    /// <summary>
    /// Adds a named publication destination for an image artifact in a container registry.
    /// </summary>
    /// <typeparam name="TRegistry">The registry resource type.</typeparam>
    /// <param name="builder">The registry builder.</param>
    /// <param name="name">The destination resource name, also used as the repository name within the registry namespace.</param>
    /// <param name="image">The source image artifact builder.</param>
    /// <returns>A builder for the registry-scoped destination image.</returns>
    /// <remarks>
    /// Each call creates a distinct resource. Destinations are additive and can share a source,
    /// including multiple repositories within one registry. References use the published content digest,
    /// never a mutable tag. No publication or permission grant is performed during model construction.
    /// Deployment publishes this destination after source preparation and registry prerequisites.
    /// Default publication tags use the same <c>aspire-deploy-yyyyMMddHHmmss</c> UTC label as compute images.
    /// Tags are retained transport addresses; consumer references use verified digests.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is invalid or the builders belong to different applications.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("addRegistryImage", MethodName = "addImage")]
    public static IResourceBuilder<DestinationImageResource> AddImage<TRegistry>(
        this IResourceBuilder<TRegistry> builder, [ResourceName] string name, IResourceBuilder<ContainerImageResource> image)
        // ATS projects the first constraint, so registry eligibility must precede IResource.
        where TRegistry : IContainerRegistry, IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ValidateApplication(builder.ApplicationBuilder, image.ApplicationBuilder);
        var resource = new DestinationImageResource(name, image.Resource, builder.Resource);
        var destination = builder.ApplicationBuilder.ExecutionContext.IsRunMode
            ? builder.ApplicationBuilder.CreateResourceBuilder(resource)
            : builder.ApplicationBuilder.AddResource(resource);
        foreach (var inferred in image.Resource.Annotations.OfType<ContainerImagePublicationAnnotation>()
            .Where(publication => publication.DefaultEnvironment is not null).ToArray())
        {
            image.Resource.Annotations.Remove(inferred);
            builder.ApplicationBuilder.Resources.Remove(inferred.Destination);
        }
        ConfigureDestination(resource);
        image.WithAnnotation(new ContainerImagePublicationAnnotation(resource, defaultEnvironment: null));

        return destination;
    }

    /// <summary>
    /// Injects a digest-qualified destination image reference into an environment variable.
    /// </summary>
    /// <typeparam name="T">The consuming resource type.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="name">The environment variable name.</param>
    /// <param name="image">The registry-scoped destination image.</param>
    /// <returns>The original consumer builder.</returns>
    /// <remarks>
    /// Preserves destination, source, and registry provenance without resolving values during construction.
    /// Deployment waits for the image publication. Azure Container Registry image references
    /// grant the consumer's managed identity pull access. This does not start a container.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The builders belong to different applications.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the canonical withEnvironment dispatcher.")]
    public static IResourceBuilder<T> WithEnvironment<T>(
        this IResourceBuilder<T> builder, string name, IResourceBuilder<DestinationImageResource> image)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(image);
        ValidateApplication(builder.ApplicationBuilder, image.ApplicationBuilder);

        return builder.WithEnvironment(name, (IExpressionValue)image.Resource);
    }

    /// <summary>
    /// Declares that a resource consumes a registry-scoped destination image.
    /// </summary>
    /// <typeparam name="T">The consuming resource type.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="image">The destination image builder.</param>
    /// <param name="name">An optional logical reference name used as the environment-variable prefix. Defaults to the destination resource name.</param>
    /// <returns>The original consumer builder.</returns>
    /// <remarks>
    /// Records image consumption for publication ordering and provider-specific pull access.
    /// Deployment waits for verified image publication. Azure Container Registry grants the
    /// consumer's managed identity pull access. Other registry providers must configure pull access separately.
    /// Injects <c>[NAME]_IMAGE</c>, <c>[NAME]_TAG</c>, <c>[NAME]_SHA256</c>,
    /// <c>[NAME]_REGISTRY</c>, and <c>[NAME]_REPOSITORY</c>, using an environment-safe uppercase prefix.
    /// Values describe the verified destination publication, not the source. SHA256 excludes the algorithm prefix.
    /// Injection honors the connection-properties reference injection flag. Repeated identical references are idempotent;
    /// distinct images cannot share an encoded prefix. Local publication and dispatch are not implemented.
    /// Use <c>WithEnvironment</c> with the image's expression properties for individual values or custom variable names.
    /// </remarks>
    /// <example>
    /// <code>
    /// var image = registry.AddImage("sandbox", source);
    /// app.WithReference(image); // SANDBOX_IMAGE, SANDBOX_TAG, SANDBOX_SHA256, SANDBOX_REGISTRY, SANDBOX_REPOSITORY
    /// app.WithEnvironment("WORKER_IMAGE", image.Resource.ImageExpression);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">A required builder is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The builders belong to different applications.</exception>
    /// <exception cref="ArgumentException">The reference name is empty or whitespace.</exception>
    /// <exception cref="DistributedApplicationException">Distinct image references use the same encoded prefix.</exception>
    [Experimental("ASPIREPIPELINES003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot AppHosts use custom resource dispatch through the canonical withReference export.")]
    public static IResourceBuilder<T> WithReference<T>(
        this IResourceBuilder<T> builder, IResourceBuilder<DestinationImageResource> image, string? name = null)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(image);
        ValidateApplication(builder.ApplicationBuilder, image.ApplicationBuilder);
        if (name is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        }
        var prefix = $"{EnvironmentVariableNameEncoder.Encode(name ?? image.Resource.Name).ToUpperInvariant()}_";
        var existing = builder.Resource.Annotations.OfType<DestinationImageReferenceAnnotation>()
            .FirstOrDefault(reference => string.Equals(reference.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!ReferenceEquals(existing.Image, image.Resource))
            {
                throw new DistributedApplicationException(
                    $"Image references '{existing.Image.Name}' and '{image.Resource.Name}' on resource '{builder.Resource.Name}' both use prefix '{prefix}'. Use unique name values when calling WithReference.");
            }

            return builder;
        }
        if (!builder.Resource.Annotations.OfType<DestinationImageReferenceAnnotation>()
            .Any(reference => ReferenceEquals(reference.Image, image.Resource)))
        {
            builder.WithRelationship(image.Resource, KnownRelationshipTypes.Reference);
        }
        builder.WithAnnotation(new DestinationImageReferenceAnnotation(image.Resource, prefix));
        builder.Resource.TryGetLastAnnotation<ReferenceEnvironmentInjectionAnnotation>(out var injection);
        if ((injection?.Flags ?? ReferenceEnvironmentInjectionFlags.All).HasFlag(ReferenceEnvironmentInjectionFlags.ConnectionProperties))
        {
            builder.WithEnvironment($"{prefix}IMAGE", image.Resource.ImageExpression)
                .WithEnvironment($"{prefix}TAG", image.Resource.TagExpression)
                .WithEnvironment($"{prefix}SHA256", image.Resource.Sha256Expression)
                .WithEnvironment($"{prefix}REGISTRY", image.Resource.RegistryExpression)
                .WithEnvironment($"{prefix}REPOSITORY", image.Resource.RepositoryExpression);
        }

        return builder;
    }

    private static void ValidateApplication(IDistributedApplicationBuilder first, IDistributedApplicationBuilder second)
    {
        if (!ReferenceEquals(first, second))
        {
            throw new ArgumentException("The image, registry, and consumer must belong to the same distributed application.");
        }
    }

    internal static void ConfigureDestination(DestinationImageResource resource) =>
        resource.Annotations.Add(new ManifestPublishingCallbackAnnotation(context => WriteDestinationManifestAsync(context, resource)));

    private static async Task WriteSourceManifestAsync(ManifestPublishingContext context, ContainerImageResource resource)
    {
        context.Writer.WriteString("type", "containerimage.v0");
        var source = resource.GetSource();
        if (source.Dockerfile is not null)
        {
            await context.WriteBuildContextAsync(resource).ConfigureAwait(false);
        }
        else
        {
            context.Writer.WriteString("source", source.Image);
        }
        context.Writer.WriteStartObject("publications");
        foreach (var publication in resource.GetPublications().OrderBy(publication => publication.Destination.Name, StringComparers.ResourceName))
        {
            context.Writer.WriteStartObject(publication.Destination.Name);
            context.Writer.WriteString("registry", publication.Registry.Name);
            context.Writer.WriteString("image", publication.Destination.ValueExpression);
            context.TryAddDependentResources(publication.Destination);
            context.Writer.WriteEndObject();
        }
        context.Writer.WriteEndObject();
    }

    private static Task WriteDestinationManifestAsync(ManifestPublishingContext context, DestinationImageResource resource)
    {
        context.Writer.WriteString("type", "containerimagepublication.v0");
        context.Writer.WriteString("source", resource.Source.Name);
        context.Writer.WriteString("registry", resource.Parent.Name);
        var registry = (IContainerRegistry)resource.Parent;
        context.Writer.WriteString("registryEndpoint", registry.Endpoint.ValueExpression);
        context.Writer.WriteString("repository", registry.Repository is { ValueExpression.Length: > 0 }
            ? ReferenceExpression.Create($"{registry.Repository}/{resource.Name.ToLowerInvariant()}").ValueExpression
            : resource.Name.ToLowerInvariant());
        context.Writer.WriteString("image", resource.GetImageExpression().ValueExpression);
        // The digest is a publication output, not a value inferred from a mutable source tag.
        context.TryAddDependentResources(resource.Source);
        context.TryAddDependentResources(resource.Parent);

        return Task.CompletedTask;
    }
}
