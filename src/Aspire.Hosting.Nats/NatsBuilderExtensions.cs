// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Nats;
using Aspire.NATS.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding NATS resources to the application model.
/// </summary>
public static class NatsBuilderExtensions
{
    /// <summary>
    /// Adds a NATS server resource to the application model. A container is used for local development.
    /// This configures a default user name and password for the NATS server.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="NatsContainerImageTags.Tag"/> tag of the <inheritdoc cref="NatsContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for NATS server.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use <see cref="AddNatsForPolyglot"/> instead.</remarks>
    [AspireExportIgnore(Reason = "Use the dedicated polyglot overload instead.")]
    public static IResourceBuilder<NatsServerResource> AddNats(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? port)
    {
        return AddNats(builder, name, port, null);
    }

    /// <summary>
    /// Adds a NATS server resource to the application model. A container is used for local development.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="NatsContainerImageTags.Tag"/> tag of the <inheritdoc cref="NatsContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for NATS server.</param>
    /// <param name="userName">The parameter used to provide the user name for the NATS resource. If <see langword="null"/> a default value will be used.</param>
    /// <param name="password">The parameter used to provide the administrator password for the NATS resource. If <see langword="null"/> a random password will be generated.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use <see cref="AddNatsForPolyglot"/> instead.</remarks>
    [AspireExportIgnore(Reason = "Use the dedicated polyglot overload instead.")]
    public static IResourceBuilder<NatsServerResource> AddNats(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? port = null,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", special: false);

        var nats = new NatsServerResource(name, userName?.Resource, passwordParameter);

        NatsConnection? natsConnection = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(nats, async (@event, ct) =>
        {
            var connectionString = await nats.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
            ?? throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{nats.Name}' resource but the connection string was null.");

            var options = NatsOpts.Default with
            {
                LoggerFactory = @event.Services.GetRequiredService<ILoggerFactory>(),
            };

            options = options with
            {
                Url = connectionString,
                AuthOpts = new()
                {
                    Username = await nats.UserNameReference.GetValueAsync(ct).ConfigureAwait(false),
                    Password = await nats.PasswordParameter!.GetValueAsync(ct).ConfigureAwait(false),
                }
            };

            natsConnection = new NatsConnection(options);
        });

        var healthCheckKey = $"{name}_check";
        builder.Services.AddHealthChecks()
          .Add(new HealthCheckRegistration(
              healthCheckKey,
              sp => new NatsHealthCheck(natsConnection!),
              failureStatus: default,
              tags: default,
              timeout: default));

        return builder.AddResource(nats)
            .WithEndpoint(targetPort: 4222, port: port, name: NatsServerResource.PrimaryEndpointName)
            .WithImage(NatsContainerImageTags.Image, NatsContainerImageTags.Tag)
            .WithImageRegistry(NatsContainerImageTags.Registry)
            .WithHealthCheck(healthCheckKey)
            .WithArgs(context =>
            {
                context.Args.Add("--user");
                context.Args.Add(nats.UserNameReference);
                context.Args.Add("--pass");
                context.Args.Add(nats.PasswordParameter!);
            });
    }

    /// <summary>
    /// Adds a NATS server resource to the application model.
    /// </summary>
    [AspireExport("addNats")]
    internal static IResourceBuilder<NatsServerResource> AddNatsForPolyglot(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? port = null,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
        => AddNats(builder, name, port, userName, password);

    /// <summary>
    /// Adds JetStream support to the NATS server resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="srcMountPath">Optional mount path providing persistence between restarts.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete("This method is obsolete and will be removed in a future version. Use the overload without the srcMountPath parameter and WithDataBindMount extension instead if you want to keep data locally.")]
    public static IResourceBuilder<NatsServerResource> WithJetStream(this IResourceBuilder<NatsServerResource> builder, string? srcMountPath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var args = new List<string> { "-js" };
        if (srcMountPath != null)
        {
            args.Add("-sd");
            args.Add("/data");
            builder.WithBindMount(srcMountPath, "/data");
        }

        return builder.WithArgs(args.ToArray());
    }

    /// <summary>
    /// Adds JetStream support to the NATS server resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NatsServerResource> WithJetStream(this IResourceBuilder<NatsServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new NatsJetStreamAnnotation())
            .WithArgs("-js");
    }

    /// <summary>
    /// Adds a named volume for the data folder to a NATS container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NatsServerResource> WithDataVolume(this IResourceBuilder<NatsServerResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/var/lib/nats",
                isReadOnly)
            .WithArgs("-sd", "/var/lib/nats");
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a NATS container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NatsServerResource> WithDataBindMount(this IResourceBuilder<NatsServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/var/lib/nats", isReadOnly)
            .WithArgs("-sd", "/var/lib/nats");
    }

    /// <summary>
    /// Adds cluster configurations to a NATS container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="routesLocator">A function that returns a list of endpoint references for other cluster members.</param>
    /// <param name="clusterName">The name of the cluster. Required if JetStream has been enabled on this NATS resource.</param>
    /// <param name="clusterPort">The port for the cluster. If not provided, the conventional NATS cluster port will be used: <c>4248</c>. This port isn't exposed on the host.</param>
    [AspireExport]
    public static IResourceBuilder<NatsServerResource> WithCluster(
        this IResourceBuilder<NatsServerResource> builder,
        Func<IReadOnlyList<EndpointReference>> routesLocator,
        string? clusterName = null,
        int? clusterPort = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(routesLocator);

        builder = builder
            .WithEndpoint(
                targetPort: NatsServerResource.ConventionalClusterPort,
                port: clusterPort,
                name: NatsServerResource.ClusterEndpointName
            )
            .WithArgs(context =>
            {
                var otherRoutes = routesLocator();
                if (otherRoutes is [])
                {
                    return;
                }

                context.Args.Add("--cluster");
                context.Args.Add($"nats://0.0.0.0:{NatsServerResource.ConventionalClusterPort}");

                if (clusterName is not (null or ""))
                {
                    // NOTE: If unset, NATS will create one automatically, but only when JetStream isn't enabled.
                    context.Args.Add("--cluster_name");
                    context.Args.Add(clusterName);
                }
                else if (builder.Resource.Annotations.OfType<NatsJetStreamAnnotation>().Any())
                {
                    throw new ArgumentException($"The '{nameof(clusterName)}' parameter must be provided when enabling JetStream support.");
                }

                var routeUrls = otherRoutes.Select(r => $"{NatsServerResource.PrimaryNatsSchemeName}://{r.Resource.Name}:{r.TargetPort}");
                context.Args.Add("--routes");
                context.Args.Add(string.Join(',', routeUrls));
            })
            .WithArgs();

        return builder;
    }

    /// <summary>
    /// Adds a server name to a NATS server instance.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="serverName">The server name to set. Defaults to the resource name if not provided.</param>
    /// <remarks>s
    /// This is useful when configuring a NATS cluster with JetStream enabled, where each member of the cluster must have a unique server name.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<NatsServerResource> WithServerName(
        this IResourceBuilder<NatsServerResource> builder,
        string? serverName = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithArgs("--server_name", serverName ?? builder.Resource.Name);
    }
}

internal sealed record NatsJetStreamAnnotation : IResourceAnnotation;
