// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides publishing configuration for resources backed by .NET programs.
/// </summary>
[Experimental("ASPIREPROJECTS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class DotnetProgramResourceBuilderExtensions
{
    /// <summary>
    /// Configures the .NET program to run the specified number of replicas.
    /// </summary>
    /// <typeparam name="T">The .NET program resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="replicas">The number of replicas.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot integrations export concrete resource overloads so fluent APIs preserve their concrete builder type.")]
    public static IResourceBuilder<T> WithReplicas<T>(this IResourceBuilder<T> builder, int replicas)
        where T : IDotnetProgramResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new ReplicaAnnotation(replicas));
        return builder;
    }

    /// <summary>
    /// Configures the .NET program to omit automatic forwarded-header configuration when publishing.
    /// </summary>
    /// <typeparam name="T">The .NET program resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot integrations export concrete resource overloads so fluent APIs preserve their concrete builder type.")]
    public static IResourceBuilder<T> DisableForwardedHeaders<T>(this IResourceBuilder<T> builder)
        where T : IDotnetProgramResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation<DisableForwardedHeadersAnnotation>(ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Configures which endpoints contribute environment variables for the .NET program.
    /// </summary>
    /// <typeparam name="T">The .NET program resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="filter">The endpoint inclusion filter.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExportIgnore(Reason = "Uses Func<EndpointAnnotation, bool>; polyglot app hosts use the endpoint-name dispatcher.")]
    public static IResourceBuilder<T> WithEndpointsInEnvironment<T>(
        this IResourceBuilder<T> builder,
        Func<EndpointAnnotation, bool> filter)
        where T : IDotnetProgramResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(filter);

        builder.Resource.Annotations.Add(new EndpointEnvironmentInjectionFilterAnnotation(filter));
        return builder;
    }

    /// <summary>
    /// Adds a callback that configures build-only environment variables for .NET SDK container publishing.
    /// </summary>
    /// <typeparam name="T">The .NET program resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback that configures the build environment.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="callback"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Values supplied through this callback affect the publishing MSBuild evaluation and may appear in build
    /// diagnostics. They are not a secret transport. Callbacks are evaluated in registration order for each publish build.
    /// </para>
    /// <para>
    /// When publishing a container, Aspire rejects values that control the output artifact's identity, destination,
    /// format, or target platform because downstream publishing steps use the corresponding container build options.
    /// Configure those values with <c>WithContainerBuildOptions</c>.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Integration authoring API with a raw delegate callback that is not ATS-compatible.")]
    public static IResourceBuilder<T> WithDotnetProgramBuildEnvironment<T>(
        this IResourceBuilder<T> builder,
        Func<EnvironmentCallbackContext, Task> callback)
        where T : IDotnetProgramResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new DotnetProgramBuildEnvironmentCallbackAnnotation(callback));
    }

    /// <summary>
    /// Configures a .NET program resource to publish a container image through the .NET SDK.
    /// </summary>
    /// <typeparam name="T">The .NET program resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// The resource does not implement <see cref="IComputeResource"/> or does not carry exactly one stable
    /// <see cref="IProjectMetadata"/> annotation.
    /// </exception>
    /// <remarks>
    /// This method is intended for .NET language integrations that model programs without deriving from
    /// <see cref="ProjectResource"/>. Ordinary projects created with <c>AddProject</c> are configured automatically.
    /// </remarks>
    [AspireExportIgnore(Reason = "Integration authoring API that depends on .NET project metadata and is not part of the ATS surface.")]
    public static IResourceBuilder<T> WithDotnetProgramPublishing<T>(this IResourceBuilder<T> builder)
        where T : IDotnetProgramResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource is not IComputeResource)
        {
            throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' must implement {nameof(IComputeResource)} to use .NET SDK publishing.");
        }

        _ = builder.Resource.GetProjectMetadata();
        DotnetProgramPublishing.Configure(builder.Resource);

        return builder;
    }
}
