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

    [AspireExport("addNats", Description = "Adds a NATS server resource to the application model.")]
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
    [AspireExport("withJetStream", Description = "Configures the NATS resource to enable JetStream.")]
    public static IResourceBuilder<NatsServerResource> WithJetStream(this IResourceBuilder<NatsServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithArgs("-js");
    }

    /// <summary>
    /// Adds a named volume for the data folder to a NATS container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withDataVolume", Description = "Adds a persistent data volume to the NATS resource.")]
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
    [AspireExport("withDataBindMount", Description = "Mounts a host directory as the NATS data directory.")]
    public static IResourceBuilder<NatsServerResource> WithDataBindMount(this IResourceBuilder<NatsServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/var/lib/nats", isReadOnly)
            .WithArgs("-sd", "/var/lib/nats");
    }
}
