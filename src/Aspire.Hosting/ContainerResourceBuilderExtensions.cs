// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for <see cref="IDistributedApplicationBuilder"/> to add container resources to the application.
/// </summary>
public static class ContainerResourceBuilderExtensions
{
    /// <summary>
    /// Ensures that a container resource has PipelineStepAnnotations for building and pushing.
    /// </summary>
    /// <typeparam name="T">The type of container resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    internal static IResourceBuilder<T> EnsureBuildAndPushPipelineAnnotations<T>(this IResourceBuilder<T> builder) where T : ContainerResource
    {
        builder.WithAnnotation(new PipelineStepAnnotation((factoryContext) =>
        {
            var steps = new List<PipelineStep>();

            if (builder.Resource.IsExcludedFromPublish())
            {
                return steps;
            }

            if (builder.Resource.RequiresImageBuild())
            {
                var buildStep = new PipelineStep
                {
                    Name = $"build-{builder.Resource.Name}",
                    Action = async ctx =>
                    {
                        var containerImageBuilder = ctx.Services.GetRequiredService<IResourceContainerImageManager>();
                        await containerImageBuilder.BuildImageAsync(
                            builder.Resource,
                            ctx.CancellationToken).ConfigureAwait(false);
                    },
                    Tags = [WellKnownPipelineTags.BuildCompute],
                    RequiredBySteps = [WellKnownPipelineSteps.Build],
                    DependsOnSteps = [WellKnownPipelineSteps.BuildPrereq],
                    Resource = builder.Resource
                };
                steps.Add(buildStep);
            }

            if (builder.Resource.RequiresImageBuildAndPush())
            {
                var pushStep = new PipelineStep
                {
                    Name = $"push-{builder.Resource.Name}",
                    Action = ctx => PipelineStepHelpers.PushImageToRegistryAsync(builder.Resource, ctx),
                    Tags = [WellKnownPipelineTags.PushContainerImage],
                    RequiredBySteps = [WellKnownPipelineSteps.Push],
                    Resource = builder.Resource
                };
                steps.Add(pushStep);
            }

            return steps;
        }), ResourceAnnotationMutationBehavior.Replace);

        return builder.WithAnnotation(new PipelineConfigurationAnnotation(context =>
        {
            var buildSteps = context.GetSteps(builder.Resource, WellKnownPipelineTags.BuildCompute);
            var pushSteps = context.GetSteps(builder.Resource, WellKnownPipelineTags.PushContainerImage);

            pushSteps.DependsOn(buildSteps);
            pushSteps.DependsOn(WellKnownPipelineSteps.PushPrereq);
        }), ResourceAnnotationMutationBehavior.Append);
    }

    /// <summary>
    /// Adds a container resource to the application. Uses the "latest" tag.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="image">The container image name. The tag is assumed to be "latest".</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    public static IResourceBuilder<ContainerResource> AddContainer(this IDistributedApplicationBuilder builder, [ResourceName] string name, string image)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(image);

        var container = new ContainerResource(name);
        return builder.AddResource(container)
                      .WithImage(image);
    }

    /// <summary>
    /// Adds a container resource to the application.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="image">The container image name.</param>
    /// <param name="tag">The container image tag.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    public static IResourceBuilder<ContainerResource> AddContainer(this IDistributedApplicationBuilder builder, [ResourceName] string name, string image, string tag)
    {
        return AddContainer(builder, name, image)
           .WithImageTag(tag);
    }

    /// <summary>
    /// Adds a volume to a container resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume.</param>
    /// <param name="target">The target path where the volume is mounted in the container.</param>
    /// <param name="isReadOnly">A flag that indicates if the volume should be mounted as read-only.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Volumes are used to persist file-based data generated by and used by the container. They are managed by the container runtime and can be shared among multiple containers.
    /// They are not shared with the host's file-system. To mount files from the host inside the container, call <see cref="WithBindMount{T}(IResourceBuilder{T}, string, string, bool)"/>.
    /// </para>
    /// <para>
    /// If a value for the <paramref name="name"/> of the volume is not provided, the volume is created as an "anonymous volume" and will be given a random name by the container
    /// runtime. To share a volume between multiple containers, specify the same <paramref name="name"/>.
    /// </para>
    /// <para>
    /// The <paramref name="target"/> path specifies the path the volume will be mounted inside the container's file system.
    /// </para>
    /// <example>
    /// Adds a volume named <c>data</c> that will be mounted in the container's file system at the path <c>/usr/data</c>:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithVolume("data", "/usr/data");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithVolume<T>(this IResourceBuilder<T> builder, string? name, string target, bool isReadOnly = false) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(target);

        var annotation = new ContainerMountAnnotation(name, target, ContainerMountType.Volume, isReadOnly);
        return builder.WithAnnotation(annotation);
    }

    /// <summary>
    /// Adds an anonymous volume to a container resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="target">The target path where the volume is mounted in the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Volumes are used to persist file-based data generated by and used by the container. They are managed by the container runtime and can be shared among multiple containers.
    /// They are not shared with the host's file-system. To mount files from the host inside the container, call <see cref="WithBindMount{T}(IResourceBuilder{T}, string, string, bool)"/>.
    /// </para>
    /// <para>
    /// This overload will create an "anonymous volume" and will be given a random name by the container runtime. To share a volume between multiple containers, call
    /// <see cref="WithVolume{T}(IResourceBuilder{T}, string?, string, bool)"/> and specify the same value for <c>name</c>.
    /// </para>
    /// <para>
    /// The <paramref name="target"/> path specifies the path the volume will be mounted inside the container's file system.
    /// </para>
    /// <example>
    /// Adds an anonymous volume that will be mounted in the container's file system at the path <c>/usr/data</c>:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithVolume("/usr/data");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithVolume<T>(this IResourceBuilder<T> builder, string target) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(target);

        var annotation = new ContainerMountAnnotation(null, target, ContainerMountType.Volume, false);
        return builder.WithAnnotation(annotation);
    }

    /// <summary>
    /// Adds a bind mount to a container resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source path of the mount. This is the path to the file or directory on the host, relative to the app host project directory.</param>
    /// <param name="target">The target path where the file or directory is mounted in the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Bind mounts are used to mount files or directories from the host file-system into the container. If the host doesn't require access to the files, consider
    /// using volumes instead via <see cref="WithVolume{T}(IResourceBuilder{T}, string?, string, bool)"/>.
    /// </para>
    /// <para>
    /// The <paramref name="source"/> path specifies the path of the file or directory on the host that will be mounted in the container. If the path is not absolute,
    /// it will be evaluated relative to the app host project directory path.
    /// </para>
    /// <para>
    /// The <paramref name="target"/> path specifies the path the file or directory will be mounted inside the container's file system.
    /// </para>
    /// <example>
    /// Adds a bind mount that will mount the <c>config</c> directory in the app host project directory, to the container's file system at the path <c>/database/config</c>,
    /// and mark it read-only so that the container cannot modify it:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithBindMount("./config", "/database/config", isReadOnly: true);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// <example>
    /// Adds a bind mount that will mount the <c>init.sh</c> file from a directory outside the app host project directory, to the container's file system at the path <c>/usr/config/initialize.sh</c>,
    /// and mark it read-only so that the container cannot modify it:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithBindMount("../containerconfig/scripts/init.sh", "/usr/config/initialize.sh", isReadOnly: true);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithBindMount<T>(this IResourceBuilder<T> builder, string source, string target, bool isReadOnly = false) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        // If the source is a rooted path, use it directly without resolution
        var sourcePath = Path.IsPathRooted(source) ? source : Path.GetFullPath(source, builder.ApplicationBuilder.AppHostDirectory);
        var annotation = new ContainerMountAnnotation(sourcePath, target, ContainerMountType.BindMount, isReadOnly);
        return builder.WithAnnotation(annotation);
    }

    /// <summary>
    /// Sets the Entrypoint for the container.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="entrypoint">The new entrypoint for the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEntrypoint<T>(this IResourceBuilder<T> builder, string entrypoint) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(entrypoint);

        builder.Resource.Entrypoint = entrypoint;
        return builder;
    }

    /// <summary>
    /// Allows overriding the image tag on a container.
    /// </summary>
    /// <typeparam name="T">Type of container resource.</typeparam>
    /// <param name="builder">Builder for the container resource.</param>
    /// <param name="tag">Tag value.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithImageTag<T>(this IResourceBuilder<T> builder, string tag) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tag);

        if (builder.Resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault() is { } existingImageAnnotation)
        {
            existingImageAnnotation.Tag = tag;

            // If there's a DockerfileBuildAnnotation with an image tag, update it as well
            // so that the user's explicit tag preference is respected
            if (builder.Resource.Annotations.OfType<DockerfileBuildAnnotation>().SingleOrDefault() is { } buildAnnotation &&
                !string.IsNullOrEmpty(buildAnnotation.ImageTag))
            {
                buildAnnotation.ImageTag = tag;
            }

            return builder;
        }

        return ThrowResourceIsNotContainer(builder);
    }

    /// <summary>
    /// Allows overriding the image registry on a container.
    /// </summary>
    /// <typeparam name="T">Type of container resource.</typeparam>
    /// <param name="builder">Builder for the container resource.</param>
    /// <param name="registry">Registry value.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithImageRegistry<T>(this IResourceBuilder<T> builder, string? registry) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault() is { } existingImageAnnotation)
        {
            existingImageAnnotation.Registry = registry;
            return builder;
        }

        return ThrowResourceIsNotContainer(builder);
    }

    /// <summary>
    /// Allows overriding the image on a container.
    /// </summary>
    /// <typeparam name="T">Type of container resource.</typeparam>
    /// <param name="builder">Builder for the container resource.</param>
    /// <param name="image">Image value.</param>
    /// <param name="tag">Tag value.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithImage<T>(this IResourceBuilder<T> builder, string image, string? tag = null) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(image);

        var parsedReference = ContainerReferenceParser.Parse(image);

        if (tag is { } && parsedReference.Tag is { })
        {
            throw new InvalidOperationException("Ambiguous tags - a tag was provided on both the 'tag' and 'image' parameters");
        }

        if (tag is { } && parsedReference.Digest is { })
        {
            throw new ArgumentOutOfRangeException(nameof(tag), "Tag conflicts with digest provided on the 'image' parameter");
        }

        // For continuity with 9.0 and earlier behaviour, keep the registry and image combined.
        var parsedRegistryAndImage = parsedReference.Registry is { }
            ? $"{parsedReference.Registry}/{parsedReference.Image}"
            : parsedReference.Image;

        if (builder.Resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault() is { } imageAnnotation)
        {
            imageAnnotation.Image = parsedRegistryAndImage;
        }
        else
        {
            imageAnnotation = new ContainerImageAnnotation { Image = parsedRegistryAndImage };
            builder.Resource.Annotations.Add(imageAnnotation);
        }

        if (parsedReference.Digest is { })
        {
            const string prefix = "sha256:";
            if (!parsedReference.Digest.StartsWith(prefix))
            {
                throw new ArgumentOutOfRangeException(nameof(image), parsedReference.Digest, "invalid digest format");
            }

            var digest = parsedReference.Digest[prefix.Length..];
            imageAnnotation.SHA256 = digest;
        }
        else
        {
            imageAnnotation.Tag = parsedReference.Tag ?? tag ?? "latest";
        }

        // If there's a DockerfileBuildAnnotation with an image name/tag, clear them
        // so that the user's explicit image preference is respected
        if (builder.Resource.Annotations.OfType<DockerfileBuildAnnotation>().SingleOrDefault() is { } buildAnnotation)
        {
            buildAnnotation.ImageName = null;
            buildAnnotation.ImageTag = null;
        }

        return builder;
    }

    /// <summary>
    /// Allows setting the image to a specific sha256 on a container.
    /// </summary>
    /// <typeparam name="T">Type of container resource.</typeparam>
    /// <param name="builder">Builder for the container resource.</param>
    /// <param name="sha256">Registry value.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithImageSHA256<T>(this IResourceBuilder<T> builder, string sha256) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sha256);

        if (builder.Resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault() is { } existingImageAnnotation)
        {
            existingImageAnnotation.SHA256 = sha256;
            return builder;
        }

        return ThrowResourceIsNotContainer(builder);
    }

    /// <summary>
    /// Adds a callback to be executed with a list of arguments to add to the container runtime run command when a container resource is started.
    /// </summary>
    /// <remarks>
    /// This is intended to pass additional arguments to the underlying container runtime run command to enable advanced features such as exposing GPUs to the container. To pass runtime arguments to the actual container, use the <see cref="ResourceBuilderExtensions.WithArgs{T}(IResourceBuilder{T}, string[])"/> method.
    /// </remarks>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">Builder for the container resource.</param>
    /// <param name="args">The arguments to be passed to the container runtime run command when the container resource is started.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithContainerRuntimeArgs<T>(this IResourceBuilder<T> builder, params string[] args) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithContainerRuntimeArgs(context => context.Args.AddRange(args));
    }

    /// <summary>
    /// Adds a callback to be executed with a list of arguments to add to the container runtime run command when a container resource is started.
    /// </summary>
    /// <remarks>
    /// This is intended to pass additional arguments to the underlying container runtime run command to enable advanced features such as exposing GPUs to the container. To pass runtime arguments to the actual container, use the <see cref="ResourceBuilderExtensions.WithArgs{T}(IResourceBuilder{T}, Action{CommandLineArgsCallbackContext})"/> method.
    /// </remarks>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">Builder for the container resource.</param>
    /// <param name="callback">A callback that allows for deferred execution for computing arguments. This runs after resources have been allocation by the orchestrator and allows access to other resources to resolve computed data, e.g. connection strings, ports.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithContainerRuntimeArgs<T>(this IResourceBuilder<T> builder, Action<ContainerRuntimeArgsCallbackContext> callback) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithContainerRuntimeArgs(context =>
        {
            callback(context);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Adds a callback to be executed with a list of arguments to add to the container runtime run command when a container resource is started.
    /// </summary>
    /// <remarks>
    /// This is intended to pass additional arguments to the underlying container runtime run command to enable advanced features such as exposing GPUs to the container. To pass runtime arguments to the actual container, use the <see cref="ResourceBuilderExtensions.WithArgs{T}(IResourceBuilder{T}, Func{CommandLineArgsCallbackContext, Task})"/> method.
    /// </remarks>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">Builder for the container resource.</param>
    /// <param name="callback">A callback that allows for deferred execution for computing arguments. This runs after resources have been allocation by the orchestrator and allows access to other resources to resolve computed data, e.g. connection strings, ports.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithContainerRuntimeArgs<T>(this IResourceBuilder<T> builder, Func<ContainerRuntimeArgsCallbackContext, Task> callback) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        var annotation = new ContainerRuntimeArgsCallbackAnnotation(callback);
        return builder.WithAnnotation(annotation);
    }

    /// <summary>
    /// Sets the lifetime behavior of the container resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">Builder for the container resource.</param>
    /// <param name="lifetime">The lifetime behavior of the container resource. The defaults behavior is <see cref="ContainerLifetime.Session"/>.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <example>
    /// Marking a container resource to have a <see cref="ContainerLifetime.Persistent"/> lifetime.
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithLifetime(ContainerLifetime.Persistent);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithLifetime<T>(this IResourceBuilder<T> builder, ContainerLifetime lifetime) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new ContainerLifetimeAnnotation { Lifetime = lifetime }, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Sets the pull policy for the container resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">Builder for the container resource.</param>
    /// <param name="pullPolicy">The pull policy behavior for the container resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithImagePullPolicy<T>(this IResourceBuilder<T> builder, ImagePullPolicy pullPolicy) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new ContainerImagePullPolicyAnnotation { ImagePullPolicy = pullPolicy }, ResourceAnnotationMutationBehavior.Replace);
    }
    private static IResourceBuilder<T> ThrowResourceIsNotContainer<T>(IResourceBuilder<T> builder) where T : ContainerResource
    {
        throw new InvalidOperationException($"The resource '{builder.Resource.Name}' does not have a container image specified. Use WithImage to specify the container image and tag.");
    }

    /// <summary>
    /// Changes the resource to be published as a container in the manifest.
    /// </summary>
    /// <param name="builder">Resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> PublishAsContainer<T>(this IResourceBuilder<T> builder) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithManifestPublishingCallback(context => context.WriteContainerAsync(builder.Resource));
    }

    /// <summary>
    /// Causes .NET Aspire to build the specified container image from a Dockerfile.
    /// </summary>
    /// <typeparam name="T">Type parameter specifying any type derived from <see cref="ContainerResource"/>/</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    /// <param name="contextPath">Path to be used as the context for the container image build.</param>
    /// <param name="dockerfilePath">Path to the Dockerfile relative to the <paramref name="contextPath"/>. Defaults to "Dockerfile" if not specified.</param>
    /// <param name="stage">The stage representing the image to be published in a multi-stage Dockerfile.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// When this method is called an annotation is added to the <see cref="ContainerResource"/> that specifies the context path and
    /// Dockerfile path to be used when building the container image. These details are then used by the orchestrator to build the image
    /// before using that image to start the container.
    /// </para>
    /// <para>
    /// The <paramref name="contextPath"/> is relative to the AppHost directory unless it is a fully qualified path.
    /// The <paramref name="dockerfilePath"/> is relative to the <paramref name="contextPath"/> unless it is a fully qualified path.
    /// If the <paramref name="dockerfilePath"/> is not provided, it defaults to "Dockerfile" in the <paramref name="contextPath"/>.
    /// </para>
    /// <para>
    /// When generating the manifest for deployment tools, the <see cref="ContainerResourceBuilderExtensions.WithDockerfile{T}(IResourceBuilder{T}, string, string?, string?)"/>
    /// method results in an additional attribute being added to the `container.v0` resource type which contains the configuration
    /// necessary to allow the deployment tool to build the container image prior to deployment.
    /// </para>
    /// <example>
    /// Creates a container called <c>mycontainer</c> with an image called <c>myimage</c>.
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithDockerfile("path/to/context");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithDockerfile<T>(this IResourceBuilder<T> builder, string contextPath, string? dockerfilePath = null, string? stage = null) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(contextPath);

        var fullyQualifiedContextPath = Path.GetFullPath(contextPath, builder.ApplicationBuilder.AppHostDirectory)
                                           .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        dockerfilePath ??= "Dockerfile";

        var fullyQualifiedDockerfilePath = Path.GetFullPath(dockerfilePath, fullyQualifiedContextPath);

        var imageName = ImageNameGenerator.GenerateImageName(builder);
        var imageTag = ImageNameGenerator.GenerateImageTag(builder);
        var annotation = new DockerfileBuildAnnotation(fullyQualifiedContextPath, fullyQualifiedDockerfilePath, stage);

        // Add default container build options annotation that uses the DockerfileBuildAnnotation's ImageName and ImageTag
        var defaultContainerBuildOptions = new ContainerBuildOptionsCallbackAnnotation(context =>
        {
            // Use DockerfileBuildAnnotation values if set, otherwise fall back to resource name
            if (context.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileAnnotation))
            {
                context.LocalImageName = dockerfileAnnotation.ImageName ?? context.Resource.Name;
                context.LocalImageTag = dockerfileAnnotation.ImageTag ?? "latest";
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            }
            else
            {
                context.LocalImageName = context.Resource.Name;
                context.LocalImageTag = "latest";
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            }
            context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
        });

        // If there's already a ContainerImageAnnotation, don't overwrite it.
        // Instead, store the generated image name and tag on the DockerfileBuildAnnotation.
        if (builder.Resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault() is { })
        {
            annotation.ImageName = imageName;
            annotation.ImageTag = imageTag;
            return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace)
                          .WithAnnotation(defaultContainerBuildOptions)
                          .EnsureBuildAndPushPipelineAnnotations();
        }

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace)
                      .WithAnnotation(defaultContainerBuildOptions)
                      .WithImageRegistry(registry: null)
                      .WithImage(imageName)
                      .WithImageTag(imageTag)
                      .EnsureBuildAndPushPipelineAnnotations();
    }

    /// <summary>
    /// Builds the specified container image from a Dockerfile generated by a synchronous factory function.
    /// </summary>
    /// <typeparam name="T">Type parameter specifying any type derived from <see cref="ContainerResource"/>.</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    /// <param name="contextPath">Path to be used as the context for the container image build.</param>
    /// <param name="dockerfileFactory">A synchronous function that returns the Dockerfile content as a string.</param>
    /// <param name="stage">The stage representing the image to be published in a multi-stage Dockerfile.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// When this method is called, an annotation is added to the <see cref="ContainerResource"/> that specifies the context path
    /// and a factory function that generates Dockerfile content. The factory is invoked at build time to produce the Dockerfile,
    /// which is then written to a temporary file and used by the orchestrator to build the container image.
    /// </para>
    /// <para>
    /// The <paramref name="contextPath"/> is relative to the AppHost directory unless it is fully qualified.
    /// </para>
    /// <para>
    /// The factory function is invoked once during the build process to generate the Dockerfile content.
    /// The output is trusted and not validated.
    /// </para>
    /// <example>
    /// Creates a container called <c>mycontainer</c> with a dynamically generated Dockerfile.
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithDockerfileFactory("path/to/context", context =>
    ///        {
    ///            return "FROM alpine:latest\nRUN echo 'Hello World'";
    ///        });
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithDockerfileFactory<T>(this IResourceBuilder<T> builder, string contextPath, Func<DockerfileFactoryContext, string> dockerfileFactory, string? stage = null) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dockerfileFactory);

        return builder.WithDockerfileFactory(contextPath, context => Task.FromResult(dockerfileFactory(context)), stage);
    }

    /// <summary>
    /// Builds the specified container image from a Dockerfile generated by an asynchronous factory function.
    /// </summary>
    /// <typeparam name="T">Type parameter specifying any type derived from <see cref="ContainerResource"/>.</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    /// <param name="contextPath">Path to be used as the context for the container image build.</param>
    /// <param name="dockerfileFactory">An asynchronous function that returns the Dockerfile content as a string.</param>
    /// <param name="stage">The stage representing the image to be published in a multi-stage Dockerfile.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// When this method is called, an annotation is added to the <see cref="ContainerResource"/> that specifies the context path
    /// and a factory function that generates Dockerfile content. The factory is invoked at build time to produce the Dockerfile,
    /// which is then written to a temporary file and used by the orchestrator to build the container image.
    /// </para>
    /// <para>
    /// The <paramref name="contextPath"/> is relative to the AppHost directory unless it is fully qualified.
    /// </para>
    /// <para>
    /// The factory function is invoked once during the build process to generate the Dockerfile content.
    /// The output is trusted and not validated.
    /// </para>
    /// <example>
    /// Creates a container called <c>mycontainer</c> with a dynamically generated Dockerfile.
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithDockerfileFactory("path/to/context", async context =>
    ///        {
    ///            var template = await File.ReadAllTextAsync("template.dockerfile", context.CancellationToken);
    ///            return template.Replace("{{VERSION}}", "1.0");
    ///        });
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithDockerfileFactory<T>(this IResourceBuilder<T> builder, string contextPath, Func<DockerfileFactoryContext, Task<string>> dockerfileFactory, string? stage = null) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(contextPath);
        ArgumentNullException.ThrowIfNull(dockerfileFactory);

        var fullyQualifiedContextPath = Path.GetFullPath(contextPath, builder.ApplicationBuilder.AppHostDirectory)
                                           .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Create a unique temporary Dockerfile path for this resource using the directory service
        var directoryService = builder.ApplicationBuilder.FileSystemService;
        var tempDockerfilePath = directoryService.TempDirectory.CreateTempFile().Path;

        var imageName = ImageNameGenerator.GenerateImageName(builder);
        var imageTag = ImageNameGenerator.GenerateImageTag(builder);

        var annotation = new DockerfileBuildAnnotation(fullyQualifiedContextPath, tempDockerfilePath, stage)
        {
            DockerfileFactory = dockerfileFactory
        };

        // Add default container build options annotation that uses the DockerfileBuildAnnotation's ImageName and ImageTag
        var defaultContainerBuildOptions = new ContainerBuildOptionsCallbackAnnotation(context =>
        {
            // Use DockerfileBuildAnnotation values if set, otherwise fall back to resource name
            if (context.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileAnnotation))
            {
                context.LocalImageName = dockerfileAnnotation.ImageName ?? context.Resource.Name;
                context.LocalImageTag = dockerfileAnnotation.ImageTag ?? "latest";
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            }
            else
            {
                context.LocalImageName = context.Resource.Name;
                context.LocalImageTag = "latest";
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            }
            context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
        });

        // If there's already a ContainerImageAnnotation, don't overwrite it.
        // Instead, store the generated image name and tag on the DockerfileBuildAnnotation.
        if (builder.Resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault() is { })
        {
            annotation.ImageName = imageName;
            annotation.ImageTag = imageTag;
            return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace)
                          .WithAnnotation(defaultContainerBuildOptions, ResourceAnnotationMutationBehavior.Append)
                          .EnsureBuildAndPushPipelineAnnotations();
        }

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Replace)
                      .WithAnnotation(defaultContainerBuildOptions, ResourceAnnotationMutationBehavior.Append)
                      .WithImageRegistry(registry: null)
                      .WithImage(imageName)
                      .WithImageTag(imageTag)
                      .EnsureBuildAndPushPipelineAnnotations();
    }

    /// <summary>
    /// Adds a Dockerfile to the application model that can be treated like a container resource.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="contextPath">Path to be used as the context for the container image build.</param>
    /// <param name="dockerfilePath">Path to the Dockerfile relative to the <paramref name="contextPath"/>. Defaults to "Dockerfile" if not specified.</param>
    /// <param name="stage">The stage representing the image to be published in a multi-stage Dockerfile.</param>
    /// <returns>A <see cref="IResourceBuilder{ContainerResource}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The <paramref name="contextPath"/> is relative to the AppHost directory unless it is a fully qualified path.
    /// The <paramref name="dockerfilePath"/> is relative to the <paramref name="contextPath"/> unless it is a fully qualified path.
    /// If the <paramref name="dockerfilePath"/> is not provided, it defaults to "Dockerfile" in the <paramref name="contextPath"/>.
    /// </para>
    /// <para>
    /// When generating the manifest for deployment tools, the <see cref="AddDockerfile(IDistributedApplicationBuilder, string, string, string?, string?)"/>
    /// method results in an additional attribute being added to the `container.v1` resource type which contains the configuration
    /// necessary to allow the deployment tool to build the container image prior to deployment.
    /// </para>
    /// <example>
    /// Creates a container called <c>mycontainer</c> based on a Dockerfile in the context path <c>path/to/context</c>.
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddDockerfile("mycontainer", "path/to/context");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<ContainerResource> AddDockerfile(this IDistributedApplicationBuilder builder, [ResourceName] string name, string contextPath, string? dockerfilePath = null, string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(contextPath);

        return builder.AddContainer(name, "placeholder") // Image name will be replaced by WithDockerfile.
                      .WithDockerfile(contextPath, dockerfilePath, stage);
    }

    /// <summary>
    /// Adds a Dockerfile to the application model that can be treated like a container resource, with the Dockerfile content generated by a synchronous factory function.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="contextPath">Path to be used as the context for the container image build.</param>
    /// <param name="dockerfileFactory">A synchronous function that returns the Dockerfile content as a string.</param>
    /// <param name="stage">The stage representing the image to be published in a multi-stage Dockerfile.</param>
    /// <returns>A <see cref="IResourceBuilder{ContainerResource}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The <paramref name="contextPath"/> is relative to the AppHost directory unless it is fully qualified.
    /// </para>
    /// <para>
    /// The factory function is invoked once during the build process to generate the Dockerfile content.
    /// The output is trusted and not validated.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<ContainerResource> AddDockerfileFactory(this IDistributedApplicationBuilder builder, [ResourceName] string name, string contextPath, Func<DockerfileFactoryContext, string> dockerfileFactory, string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(contextPath);
        ArgumentNullException.ThrowIfNull(dockerfileFactory);

        return builder.AddContainer(name, "placeholder") // Image name will be replaced by WithDockerfileFactory.
                      .WithDockerfileFactory(contextPath, dockerfileFactory, stage);
    }

    /// <summary>
    /// Adds a Dockerfile to the application model that can be treated like a container resource, with the Dockerfile content generated by an asynchronous factory function.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="contextPath">Path to be used as the context for the container image build.</param>
    /// <param name="dockerfileFactory">An asynchronous function that returns the Dockerfile content as a string.</param>
    /// <param name="stage">The stage representing the image to be published in a multi-stage Dockerfile.</param>
    /// <returns>A <see cref="IResourceBuilder{ContainerResource}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The <paramref name="contextPath"/> is relative to the AppHost directory unless it is fully qualified.
    /// </para>
    /// <para>
    /// The factory function is invoked once during the build process to generate the Dockerfile content.
    /// The output is trusted and not validated.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<ContainerResource> AddDockerfileFactory(this IDistributedApplicationBuilder builder, [ResourceName] string name, string contextPath, Func<DockerfileFactoryContext, Task<string>> dockerfileFactory, string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(contextPath);
        ArgumentNullException.ThrowIfNull(dockerfileFactory);

        return builder.AddContainer(name, "placeholder") // Image name will be replaced by WithDockerfileFactory.
                      .WithDockerfileFactory(contextPath, dockerfileFactory, stage);
    }

    /// <summary>
    /// Adds a Dockerfile to the application model that can be treated like a container resource, with the Dockerfile generated programmatically using the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="contextPath">Path to be used as the context for the container image build.</param>
    /// <param name="callback">A callback that uses the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API to construct the Dockerfile.</param>
    /// <param name="stage">The stage representing the image to be published in a multi-stage Dockerfile.</param>
    /// <returns>A <see cref="IResourceBuilder{ContainerResource}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method provides a programmatic way to build Dockerfiles using the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API
    /// instead of string manipulation.
    /// </para>
    /// <para>
    /// The <paramref name="contextPath"/> is relative to the AppHost directory unless it is fully qualified.
    /// </para>
    /// <example>
    /// Creates a container with a programmatically built Dockerfile:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddDockerfileBuilder("mycontainer", "path/to/context", context =>
    /// {
    ///     context.Builder.From("alpine:latest")
    ///         .WorkDir("/app")
    ///         .Copy(".", ".")
    ///         .Cmd(["./myapp"]);
    ///     return Task.CompletedTask;
    /// });
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<ContainerResource> AddDockerfileBuilder(this IDistributedApplicationBuilder builder, [ResourceName] string name, string contextPath, Func<DockerfileBuilderCallbackContext, Task> callback, string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(contextPath);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.AddContainer(name, "placeholder") // Image name will be replaced by WithDockerfileBuilder.
                      .WithDockerfileBuilder(contextPath, callback, stage);
    }

    /// <summary>
    /// Adds a Dockerfile to the application model that can be treated like a container resource, with the Dockerfile generated programmatically using the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="contextPath">Path to be used as the context for the container image build.</param>
    /// <param name="callback">A synchronous callback that uses the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API to construct the Dockerfile.</param>
    /// <param name="stage">The stage representing the image to be published in a multi-stage Dockerfile.</param>
    /// <returns>A <see cref="IResourceBuilder{ContainerResource}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method provides a programmatic way to build Dockerfiles using the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API
    /// instead of string manipulation.
    /// </para>
    /// <para>
    /// The <paramref name="contextPath"/> is relative to the AppHost directory unless it is fully qualified.
    /// </para>
    /// <example>
    /// Creates a container with a programmatically built Dockerfile:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddDockerfileBuilder("mycontainer", "path/to/context", context =>
    /// {
    ///     context.Builder.From("node:18")
    ///         .WorkDir("/app")
    ///         .Copy("package*.json", "./")
    ///         .Run("npm ci");
    /// });
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<ContainerResource> AddDockerfileBuilder(this IDistributedApplicationBuilder builder, [ResourceName] string name, string contextPath, Action<DockerfileBuilderCallbackContext> callback, string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(contextPath);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.AddContainer(name, "placeholder") // Image name will be replaced by WithDockerfileBuilder.
                      .WithDockerfileBuilder(contextPath, callback, stage);
    }

    /// <summary>
    /// Overrides the default container name for this resource. By default Aspire generates a unique container name based on the
    /// resource name and a random postfix (or a postfix based on a hash of the AppHost project path for persistent container resources).
    /// This method allows you to override that behavior with a custom name, but could lead to naming conflicts if the specified name is not unique.
    /// </summary>
    /// <remarks>
    /// Combining this with <see cref="ContainerLifetime.Persistent"/> will allow Aspire to re-use an existing container that was not
    /// created by an Aspire AppHost.
    /// </remarks>
    /// <typeparam name="T">The type of container resource.</typeparam>
    /// <param name="builder">The resource builder for the container resource.</param>
    /// <param name="name">The desired container name. Must be a valid container name or your runtime will report an error.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithContainerName<T>(this IResourceBuilder<T> builder, string name) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        return builder.WithAnnotation(new ContainerNameAnnotation { Name = name }, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Adds a build argument when the container is build from a Dockerfile.
    /// </summary>
    /// <typeparam name="T">The type of container resource.</typeparam>
    /// <param name="builder">The resource builder for the container resource.</param>
    /// <param name="name">The name of the build argument.</param>
    /// <param name="value">The value of the build argument.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="ContainerResourceBuilderExtensions.WithBuildArg{T}(IResourceBuilder{T}, string, object)"/> is
    /// called before <see cref="ContainerResourceBuilderExtensions.WithDockerfile{T}(IResourceBuilder{T}, string, string?, string?)"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The <see cref="ContainerResourceBuilderExtensions.WithBuildArg{T}(IResourceBuilder{T}, string, object)"/> extension method
    /// adds an additional build argument the container resource to be used when the image is built. This method must be called after
    /// <see cref="ContainerResourceBuilderExtensions.WithDockerfile{T}(IResourceBuilder{T}, string, string?, string?)"/>.
    /// </para>
    /// <example>
    /// Adding a static build argument.
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithDockerfile("../mycontainer")
    ///        .WithBuildArg("CUSTOM_BRANDING", "/app/static/branding/custom");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithBuildArg<T>(this IResourceBuilder<T> builder, string name, object? value) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var annotation = builder.Resource.Annotations.OfType<DockerfileBuildAnnotation>().SingleOrDefault();

        if (annotation is null)
        {
            throw new InvalidOperationException("The resource does not have a Dockerfile build annotation. Call WithDockerfile before calling WithBuildArg.");
        }

        annotation.BuildArguments[name] = value;

        return builder;
    }

    /// <summary>
    /// Adds a build argument when the container is built from a Dockerfile.
    /// </summary>
    /// <typeparam name="T">The type of container resource.</typeparam>
    /// <param name="builder">The resource builder for the container resource.</param>
    /// <param name="name">The name of the build argument.</param>
    /// <param name="value">The resource builder for a parameter resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="ContainerResourceBuilderExtensions.WithBuildArg{T}(IResourceBuilder{T}, string, IResourceBuilder{ParameterResource})"/> is
    /// called before <see cref="ContainerResourceBuilderExtensions.WithDockerfile{T}(IResourceBuilder{T}, string, string?, string?)"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The <see cref="ContainerResourceBuilderExtensions.WithBuildArg{T}(IResourceBuilder{T}, string, IResourceBuilder{ParameterResource})"/> extension method
    /// adds an additional build argument the container resource to be used when the image is built. This method must be called after
    /// <see cref="ContainerResourceBuilderExtensions.WithDockerfile{T}(IResourceBuilder{T}, string, string?, string?)"/>.
    /// </para>
    /// <example>
    /// Adding a build argument based on a parameter..
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var branding = builder.AddParameter("branding");
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithDockerfile("../mycontainer")
    ///        .WithBuildArg("CUSTOM_BRANDING", branding);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithBuildArg<T>(this IResourceBuilder<T> builder, string name, IResourceBuilder<ParameterResource> value) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        if (value.Resource.Secret)
        {
            throw new InvalidOperationException("Cannot add secret parameter as a build argument. Use WithSecretBuildArg instead.");
        }

        return builder.WithBuildArg(name, value.Resource);
    }

    /// <summary>
    /// Adds a secret build argument when the container is built from a Dockerfile.
    /// </summary>
    /// <typeparam name="T">The type of container resource.</typeparam>
    /// <param name="builder">The resource builder for the container resource.</param>
    /// <param name="name">The name of the secret build argument.</param>
    /// <param name="value">The resource builder for a parameter resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="ContainerResourceBuilderExtensions.WithBuildSecret{T}(IResourceBuilder{T}, string, IResourceBuilder{ParameterResource})"/> is
    /// called before <see cref="ContainerResourceBuilderExtensions.WithDockerfile{T}(IResourceBuilder{T}, string, string?, string?)"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The <see cref="ContainerResourceBuilderExtensions.WithBuildSecret{T}(IResourceBuilder{T}, string, IResourceBuilder{ParameterResource})"/> extension method
    /// results in a <c>--secret</c> argument being appended to the <c>docker build</c> or <c>podman build</c> command. This overload results in an environment
    /// variable-based secret being passed to the build process. The value of the environment variable is the value of the secret referenced by the <see cref="ParameterResource"/>.
    /// </para>
    /// <example>
    /// Adding a build secret based on a parameter.
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var accessToken = builder.AddParameter("accessToken", secret: true);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithDockerfile("../mycontainer")
    ///        .WithBuildSecret("ACCESS_TOKEN", accessToken);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithBuildSecret<T>(this IResourceBuilder<T> builder, string name, IResourceBuilder<ParameterResource> value) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        var annotation = builder.Resource.Annotations.OfType<DockerfileBuildAnnotation>().SingleOrDefault();

        if (annotation is null)
        {
            throw new InvalidOperationException("The resource does not have a Dockerfile build annotation. Call WithDockerfile before calling WithSecretBuildArg.");
        }

        annotation.BuildSecrets[name] = value.Resource;

        return builder;
    }

    /// <summary>
    /// Adds a <see cref="ContainerCertificatePathsAnnotation"/> to the resource that allows overriding the default paths in the container used for certificate trust.
    /// Custom certificate trust is only supported at run time.
    /// </summary>
    /// <typeparam name="TResource">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="customCertificatesDestination">The destination path in the container where custom certificates will be copied to. If not specified, defaults to <c>/usr/local/share/ca-certificates/aspire-custom-certs/</c>.</param>
    /// <param name="defaultCertificateBundlePaths">List of default certificate bundle paths in the container that will be replaced in <see cref="CertificateTrustScope.Override"/> or <see cref="CertificateTrustScope.System"/> modes. If not specified, defaults to <c>/etc/ssl/certs/ca-certificates.crt</c> for Linux containers.</param>
    /// <param name="defaultCertificateDirectoryPaths">List of default certificate directory paths in the container that may be appended to the custom certificates directory in <see cref="CertificateTrustScope.Append"/> mode. If not specified, defaults to <c>/usr/local/share/ca-certificates/</c> for Linux containers.</param>
    /// <returns>The updated resource builder.</returns>
    public static IResourceBuilder<TResource> WithContainerCertificatePaths<TResource>(this IResourceBuilder<TResource> builder, string? customCertificatesDestination = null, List<string>? defaultCertificateBundlePaths = null, List<string>? defaultCertificateDirectoryPaths = null)
        where TResource : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new ContainerCertificatePathsAnnotation
        {
            CustomCertificatesDestination = customCertificatesDestination,
            DefaultCertificateBundles = defaultCertificateBundlePaths,
            DefaultCertificateDirectories = defaultCertificateDirectoryPaths,
        }, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Creates or updates files and/or folders at the destination path in the container.
    /// </summary>
    /// <typeparam name="T">The type of container resource.</typeparam>
    /// <param name="builder">The resource builder for the container resource.</param>
    /// <param name="destinationPath">The destination (absolute) path in the container.</param>
    /// <param name="entries">The file system entries to create.</param>
    /// <param name="defaultOwner">The default owner UID for the created or updated file system. Defaults to 0 for root if not set.</param>
    /// <param name="defaultGroup">The default group ID for the created or updated file system. Defaults to 0 for root if not set.</param>
    /// <param name="umask">The umask <see cref="UnixFileMode"/> permissions to exclude from the default file and folder permissions. This takes away (rather than granting) default permissions to files and folders without an explicit mode permission set.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// For containers with a <see cref="ContainerLifetime.Persistent"/> lifetime, changing the contents of create file entries will result in the container being recreated.
    /// Make sure any data being written to containers is idempotent for a given app model configuration. Specifically, be careful not to include any data that will be
    /// unique on a per-run basis.
    /// </para>
    /// <example>
    /// Create a directory called <c>custom-entry</c> in the container's file system at the path <c>/usr/data</c> and create a file called <c>entrypoint.sh</c> inside it with the content <c>echo hello world</c>.
    /// The default permissions for these files will be for the user or group to be able to read and write to the files, but not execute them. entrypoint.sh will be created with execution permissions for the owner.
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///     .WithContainerFiles("/usr/data", [
    ///         new ContainerDirectory
    ///         {
    ///             Name = "custom-entry",
    ///             Entries = [
    ///                 new ContainerFile
    ///                 {
    ///                     Name = "entrypoint.sh",
    ///                     Contents = "echo hello world",
    ///                     Mode = UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
    ///                 },
    ///             ],
    ///         },
    ///     ],
    ///     defaultOwner: 1000);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithContainerFiles<T>(this IResourceBuilder<T> builder, string destinationPath, IEnumerable<ContainerFileSystemItem> entries, int? defaultOwner = null, int? defaultGroup = null, UnixFileMode? umask = null) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(entries);

        var annotation = new ContainerFileSystemCallbackAnnotation
        {
            DestinationPath = destinationPath,
            Callback = (_, _) => Task.FromResult(entries),
            DefaultOwner = defaultOwner,
            DefaultGroup = defaultGroup,
            Umask = umask,
        };

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Append);
    }

    /// <summary>
    /// Creates or updates files and/or folders at the destination path in the container. Receives a callback that will be invoked
    /// when the container is started to allow the files to be created based on other resources in the application model.
    /// </summary>
    /// <typeparam name="T">The type of container resource.</typeparam>
    /// <param name="builder">The resource builder for the container resource.</param>
    /// <param name="destinationPath">The destination (absolute) path in the container.</param>
    /// <param name="callback">The callback that will be invoked when the resource is being created.</param>
    /// <param name="defaultOwner">The default owner UID for the created or updated file system. Defaults to 0 for root if not set.</param>
    /// <param name="defaultGroup">The default group ID for the created or updated file system. Defaults to 0 for root if not set.</param>
    /// <param name="umask">The umask <see cref="UnixFileMode"/> permissions to exclude from the default file and folder permissions. This takes away (rather than granting) default permissions to files and folders without an explicit mode permission set.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// For containers with a <see cref="ContainerLifetime.Persistent"/> lifetime, changing the contents of create file entries will result in the container being recreated.
    /// Make sure any data being written to containers is idempotent for a given app model configuration. Specifically, be careful not to include any data that will be
    /// unique on a per-run basis.
    /// </para>
    /// <example>
    /// Create a configuration file for every Postgres instance in the application model.
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///     .WithContainerFiles("/", (context, cancellationToken) =>
    ///     {
    ///         var appModel = context.ServiceProvider.GetRequiredService&lt;DistributedApplicationModel&gt;();
    ///         var postgresInstances = appModel.Resources.OfType&lt;PostgresDatabaseResource&gt;();
    ///
    ///         return [
    ///             new ContainerDirectory
    ///             {
    ///                 Name = ".pgweb",
    ///                 Entries = [
    ///                     new ContainerDirectory
    ///                     {
    ///                         Name = "bookmarks",
    ///                         Entries = postgresInstances.Select(instance =>
    ///                         new ContainerFile
    ///                         {
    ///                             Name = $"{instance.Name}.toml",
    ///                             Contents = instance.ToPgWebBookmark(),
    ///                             Owner = defaultOwner,
    ///                             Group = defaultGroup,
    ///                         }),
    ///                 },
    ///             ],
    ///         },
    ///     ];
    /// });
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithContainerFiles<T>(this IResourceBuilder<T> builder, string destinationPath, Func<ContainerFileSystemCallbackContext, CancellationToken, Task<IEnumerable<ContainerFileSystemItem>>> callback, int? defaultOwner = null, int? defaultGroup = null, UnixFileMode? umask = null) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(callback);

        var annotation = new ContainerFileSystemCallbackAnnotation
        {
            DestinationPath = destinationPath,
            Callback = callback,
            DefaultOwner = defaultOwner,
            DefaultGroup = defaultGroup,
            Umask = umask,
        };

        return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Append);
    }

    /// <summary>
    /// Creates or updates files and/or folders at the destination path in the container by copying them from a source path on the host.
    /// In run mode, this will copy the files from the host to the container at runtime, allowing for overriding ownership and permissions
    /// in the container. In publish mode, this will create a bind mount to the source path on the host.
    /// </summary>
    /// <typeparam name="T">The type of container resource.</typeparam>
    /// <param name="builder">The resource builder for the container resource.</param>
    /// <param name="destinationPath">The destination (absolute) path in the container.</param>
    /// <param name="sourcePath">The source path on the host to copy files from.</param>
    /// <param name="defaultOwner">The default owner UID for the created or updated file system. Defaults to 0 for root if not set.</param>
    /// <param name="defaultGroup">The default group ID for the created or updated file system. Defaults to 0 for root if not set.</param>
    /// <param name="umask">The umask <see cref="UnixFileMode"/> permissions to exclude from the default file and folder permissions. This takes away (rather than granting) default permissions to files and folders without an explicit mode permission set.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithContainerFiles<T>(this IResourceBuilder<T> builder, string destinationPath, string sourcePath, int? defaultOwner = null, int? defaultGroup = null, UnixFileMode? umask = null) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(sourcePath);

        var sourceFullPath = Path.GetFullPath(sourcePath, builder.ApplicationBuilder.AppHostDirectory);

        if (!Directory.Exists(sourceFullPath) && !File.Exists(sourceFullPath))
        {
            throw new InvalidOperationException($"The source path '{sourceFullPath}' does not exist. Ensure the path is correct and accessible.");
        }

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            // In run mode, use copied files as they allow us to configure permissions and ownership and support
            // remote execution scenarios where the source path may not be accessible from the container runtime.
            var annotation = new ContainerFileSystemCallbackAnnotation
            {
                DestinationPath = destinationPath,
                Callback = (_, _) => Task.FromResult(ContainerDirectory.GetFileSystemItemsFromPath(sourceFullPath, searchOptions: SearchOption.AllDirectories)),
                DefaultOwner = defaultOwner,
                DefaultGroup = defaultGroup,
                Umask = umask,
            };

            return builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Append);
        }
        else
        {
            // In publish mode, use a bind mount as it is better supported by publish targets
            return builder.WithBindMount(sourceFullPath, destinationPath, isReadOnly: true);
        }
    }

    /// <summary>
    /// Set whether a container resource can use proxied endpoints or whether they should be disabled for all endpoints belonging to the container.
    /// If set to <c>false</c>, endpoints belonging to the container resource will ignore the configured proxy settings and run proxy-less.
    /// </summary>
    /// <typeparam name="T">The type of container resource.</typeparam>
    /// <param name="builder">The resource builder for the container resource.</param>
    /// <param name="proxyEnabled">Should endpoints for the container resource support using a proxy?</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method is intended to support scenarios with persistent lifetime containers where it is desirable for the container to be accessible over the same
    /// port whether the Aspire application is running or not. Proxied endpoints bind ports that are only accessible while the Aspire application is running.
    /// The user needs to be careful to ensure that container endpoints are using unique ports when disabling proxy support as by default for proxy-less
    /// endpoints, Aspire will allocate the internal container port as the host port, which will increase the chance of port conflicts.
    /// </remarks>
    public static IResourceBuilder<T> WithEndpointProxySupport<T>(this IResourceBuilder<T> builder, bool proxyEnabled) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new ProxySupportAnnotation { ProxyEnabled = proxyEnabled }, ResourceAnnotationMutationBehavior.Replace);

        return builder;
    }

    /// <summary>
    /// Builds the specified container image from a Dockerfile generated by a callback using the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API.
    /// </summary>
    /// <typeparam name="T">Type parameter specifying any type derived from <see cref="ContainerResource"/>.</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    /// <param name="contextPath">Path to be used as the context for the container image build.</param>
    /// <param name="callback">A callback that uses the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API to construct the Dockerfile.</param>
    /// <param name="stage">The stage representing the image to be published in a multi-stage Dockerfile.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method provides a programmatic way to build Dockerfiles using the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API
    /// instead of string manipulation. Callbacks can be composed by calling this method multiple times - each callback will be invoked
    /// in order to build up the final Dockerfile.
    /// </para>
    /// <para>
    /// The <paramref name="contextPath"/> is relative to the AppHost directory unless it is fully qualified.
    /// </para>
    /// <example>
    /// Creates a container with a programmatically built Dockerfile using fluent API:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithDockerfileBuilder("path/to/context", context =>
    ///        {
    ///            context.Builder.From("alpine:latest")
    ///                .WorkDir("/app")
    ///                .Run("apk add curl")
    ///                .Copy(".", ".")
    ///                .Cmd(["./myapp"]);
    ///            return Task.CompletedTask;
    ///        });
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithDockerfileBuilder<T>(this IResourceBuilder<T> builder, string contextPath, Func<DockerfileBuilderCallbackContext, Task> callback, string? stage = null) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(contextPath);
        ArgumentNullException.ThrowIfNull(callback);

        var fullyQualifiedContextPath = Path.GetFullPath(contextPath, builder.ApplicationBuilder.AppHostDirectory)
                                           .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Check if there's already a DockerfileBuilderCallbackAnnotation
        var callbackAnnotation = builder.Resource.Annotations.OfType<DockerfileBuilderCallbackAnnotation>().LastOrDefault();

        if (callbackAnnotation is not null)
        {
            // Add to existing annotation
            callbackAnnotation.AddCallback(callback);
            return builder;
        }

        // Create new callback annotation
        callbackAnnotation = new DockerfileBuilderCallbackAnnotation(callback);
        builder.WithAnnotation(callbackAnnotation);

        // Create a factory that will invoke all callbacks and generate the Dockerfile
        Func<DockerfileFactoryContext, Task<string>> dockerfileFactory = async factoryContext =>
        {
            var dockerfileBuilder = new DockerfileBuilder();

            // Create the context for callbacks
            var callbackContext = new DockerfileBuilderCallbackContext(
                resource: factoryContext.Resource,
                builder: dockerfileBuilder,
                services: factoryContext.Services,
                cancellationToken: factoryContext.CancellationToken
            );

            var annotation = factoryContext.Resource.Annotations.OfType<DockerfileBuilderCallbackAnnotation>().LastOrDefault();
            if (annotation is not null)
            {
                foreach (var cb in annotation.Callbacks)
                {
                    await cb(callbackContext).ConfigureAwait(false);
                }
            }

            // Convert DockerfileBuilder to string
            using var memoryStream = new MemoryStream();
            // Use UTF8 encoding without BOM
            var utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using var writer = new StreamWriter(memoryStream, utf8WithoutBom, leaveOpen: true);
            writer.NewLine = "\n"; // Use LF line endings for Dockerfiles

            await dockerfileBuilder.WriteAsync(writer, factoryContext.CancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(factoryContext.CancellationToken).ConfigureAwait(false);

            memoryStream.Position = 0;
            using var reader = new StreamReader(memoryStream);
            var dockerfileContent = await reader.ReadToEndAsync(factoryContext.CancellationToken).ConfigureAwait(false);

            return dockerfileContent;
        };

        // Use the existing WithDockerfileFactory overload that takes a factory
        return builder.WithDockerfileFactory(contextPath, dockerfileFactory, stage);
    }

    /// <summary>
    /// Builds the specified container image from a Dockerfile generated by a synchronous callback using the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API.
    /// </summary>
    /// <typeparam name="T">Type parameter specifying any type derived from <see cref="ContainerResource"/>.</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    /// <param name="contextPath">Path to be used as the context for the container image build.</param>
    /// <param name="callback">A synchronous callback that uses the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API to construct the Dockerfile.</param>
    /// <param name="stage">The stage representing the image to be published in a multi-stage Dockerfile.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method provides a programmatic way to build Dockerfiles using the <see cref="ApplicationModel.Docker.DockerfileBuilder"/> API
    /// instead of string manipulation. Callbacks can be composed by calling this method multiple times - each callback will be invoked
    /// in order to build up the final Dockerfile.
    /// </para>
    /// <para>
    /// The <paramref name="contextPath"/> is relative to the AppHost directory unless it is fully qualified.
    /// </para>
    /// <example>
    /// Creates a container with a programmatically built Dockerfile using fluent API:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddContainer("mycontainer", "myimage")
    ///        .WithDockerfileBuilder("path/to/context", context =>
    ///        {
    ///            context.Builder.From("node:18")
    ///                .WorkDir("/app")
    ///                .Copy("package*.json", "./")
    ///                .Run("npm ci");
    ///        });
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithDockerfileBuilder<T>(this IResourceBuilder<T> builder, string contextPath, Action<DockerfileBuilderCallbackContext> callback, string? stage = null) where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithDockerfileBuilder(contextPath, context =>
        {
            callback(context);
            return Task.CompletedTask;
        }, stage);
    }

    /// <summary>
    /// Configures custom base images for generated Dockerfiles.
    /// </summary>
    /// <typeparam name="T">The type of resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="buildImage">The base image to use for the build stage. If null, uses the default build image.</param>
    /// <param name="runtimeImage">The base image to use for the runtime stage. If null, uses the default runtime image.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This extension method allows customization of the base images used in generated Dockerfiles.
    /// For multi-stage Dockerfiles (e.g., Python with UV), you can specify separate build and runtime images.
    /// </para>
    /// <example>
    /// Specify custom base images for a Python application:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddPythonApp("myapp", "path/to/app", "main.py")
    ///        .WithDockerfileBaseImage(
    ///            buildImage: "ghcr.io/astral-sh/uv:python3.12-bookworm-slim",
    ///            runtimeImage: "python:3.12-slim-bookworm");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithDockerfileBaseImage<T>(this IResourceBuilder<T> builder, string? buildImage = null, string? runtimeImage = null) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (buildImage is null && runtimeImage is null)
        {
            throw new ArgumentException($"At least one of {nameof(buildImage)} or {nameof(runtimeImage)} must be specified.", nameof(buildImage));
        }

        return builder.WithAnnotation(new DockerfileBaseImageAnnotation
        {
            BuildImage = buildImage,
            RuntimeImage = runtimeImage
        }, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Adds a network alias to container resource.
    /// </summary>
    /// <typeparam name="T">The type of container resource.</typeparam>
    /// <param name="builder">The resource builder for the container resource.</param>
    /// <param name="alias">The network alias for the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Network aliases enable DNS resolution of the container on the network by custom names.
    /// By default, containers are accessible on the network using their resource name as a DNS alias.
    /// This method allows adding additional aliases for the same container.
    /// </para>
    /// <para>
    /// Multiple aliases can be added by calling this method multiple times.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<T> WithContainerNetworkAlias<T>(this IResourceBuilder<T> builder, string alias) where T : ContainerResource
    {
        return builder.WithAnnotation(new ContainerNetworkAliasAnnotation(alias) { Network = KnownNetworkIdentifiers.DefaultAspireContainerNetwork });
    }
}

internal static class IListExtensions
{
    public static void AddRange<T>(this IList<T> list, IEnumerable<T> collection)
    {
        foreach (var item in collection)
        {
            list.Add(item);
        }
    }
}
