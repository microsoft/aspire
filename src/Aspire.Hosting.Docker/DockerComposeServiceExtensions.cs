// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for customizing Docker Compose service resources.
/// </summary>
public static class DockerComposeServiceExtensions
{
    /// <summary>
    /// Publishes the specified resource as a Docker Compose service.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The configuration action for the Docker Compose service.</param>
    /// <returns>The updated resource builder.</returns>
    /// <remarks>
    /// This method checks if the application is in publish mode. If it is, it adds a customization annotation
    /// that will be applied by the DockerComposeInfrastructure when generating the Docker Compose service.
    /// <example>
    /// <code>
    /// builder.AddContainer("redis", "redis:alpine").PublishAsDockerComposeService((resource, service) =>
    /// {
    ///     service.Name = "redis";
    /// });
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport(Description = "Publishes the resource as a Docker Compose service with custom service configuration")]
    public static IResourceBuilder<T> PublishAsDockerComposeService<T>(this IResourceBuilder<T> builder, Action<DockerComposeServiceResource, Service> configure)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        builder.ApplicationBuilder.AddDockerComposeInfrastructureCore();

        return builder.WithAnnotation(new DockerComposeServiceCustomizationAnnotation(configure));
    }

    /// <summary>
    /// Creates a placeholder for an environment variable in the Docker Compose file.
    /// </summary>
    /// <param name="manifestExpressionProvider">The manifest expression provider.</param>
    /// <param name="dockerComposeService">The Docker Compose service resource to associate the environment variable with.</param>
    /// <returns>A string representing the environment variable placeholder in Docker Compose syntax (e.g., <c>${ENV_VAR}</c>).</returns>
    /// <remarks>This overload is not available in polyglot app hosts because <see cref="IManifestExpressionProvider"/> is not ATS-compatible.</remarks>
    [AspireExportIgnore(Reason = "IManifestExpressionProvider parameters are not ATS-compatible. Use the parameter-builder overload in polyglot app hosts.")]
    public static string AsEnvironmentPlaceholder(this IManifestExpressionProvider manifestExpressionProvider, DockerComposeServiceResource dockerComposeService)
    {
        var env = manifestExpressionProvider.ValueExpression.Replace("{", "")
                 .Replace("}", "")
                 .Replace(".", "_")
                 .Replace("-", "_")
                 .ToUpperInvariant();

        return dockerComposeService.Parent.AddEnvironmentVariable(
            env,
            source: manifestExpressionProvider
        );
    }

    /// <summary>
    /// Creates a Docker Compose environment variable placeholder for the specified <see cref="ParameterResource"/>.
    /// </summary>
    /// <param name="builder">The resource builder for the parameter resource.</param>
    /// <param name="dockerComposeService">The Docker Compose service resource to associate the environment variable with.</param>
    /// <returns>A string representing the environment variable placeholder in Docker Compose syntax (e.g., <c>${ENV_VAR}</c>).</returns>
    /// <remarks>
    /// Use this overload with parameter builders returned by methods such as <see cref="ParameterResourceBuilderExtensions.AddParameter(IDistributedApplicationBuilder, string, bool)"/>.
    /// </remarks>
    [AspireExport(Description = "Creates a Docker Compose environment variable placeholder from a parameter builder")]
    public static string AsEnvironmentPlaceholder(this IResourceBuilder<ParameterResource> builder, DockerComposeServiceResource dockerComposeService)
    {
        return builder.Resource.AsEnvironmentPlaceholder(dockerComposeService);
    }

    /// <summary>
    /// Creates a Docker Compose environment variable placeholder for this <see cref="ParameterResource"/>.
    /// </summary>
    /// <param name="parameter">The parameter resource for which to create the environment variable placeholder.</param>
    /// <param name="dockerComposeService">The Docker Compose service resource to associate the environment variable with.</param>
    /// <returns>A string representing the environment variable placeholder in Docker Compose syntax (e.g., <c>${ENV_VAR}</c>).</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the builder or manifest-expression overload instead.</remarks>
    [AspireExportIgnore(Reason = "Prefer the builder or IManifestExpressionProvider overloads in polyglot app hosts to avoid duplicate asEnvironmentPlaceholder projections on ParameterResource.")]
    public static string AsEnvironmentPlaceholder(this ParameterResource parameter, DockerComposeServiceResource dockerComposeService)
    {
        // Placeholder for resolving the actual parameter value
        // https://docs.docker.com/compose/how-tos/environment-variables/variable-interpolation/#interpolation-syntax

        var env = parameter.Name.ToUpperInvariant().Replace("-", "_");

        // Treat secrets as environment variable placeholders as for now
        // this doesn't handle generation of parameter values with defaults
        return dockerComposeService.Parent.AddEnvironmentVariable(
            env,
            description: $"Parameter {parameter.Name}",
            defaultValue: null,
            source: parameter
        );
    }

    internal static string AsContainerImagePlaceholder(this DockerComposeServiceResource dockerComposeService)
    {
        var resourceInstance = dockerComposeService.TargetResource;

        var imageEnvName = $"{resourceInstance.Name.ToUpperInvariant().Replace("-", "_")}_IMAGE";

        return dockerComposeService.Parent.AddEnvironmentVariable(
             imageEnvName,
             description: $"Container image name for {resourceInstance.Name}",
             defaultValue: $"{resourceInstance.Name}:latest",
             source: new ContainerImageReference(resourceInstance)
        );
    }

    internal static string AsContainerPortPlaceholder(this DockerComposeServiceResource dockerComposeService)
    {
        var resourceInstance = dockerComposeService.TargetResource;

        var containerPortEnv = $"{resourceInstance.Name.ToUpperInvariant().Replace("-", "_")}_PORT";

        return dockerComposeService.Parent.AddEnvironmentVariable(
             containerPortEnv,
             description: $"Default container port for {resourceInstance.Name}",
             defaultValue: "8080",
             source: new ContainerPortReference(resourceInstance)
        );
    }
}
