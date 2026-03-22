// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Milvus;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Milvus resources to the application model.
/// </summary>
public static class MilvusBuilderExtensions
{
    private const int MilvusPortGrpc = 19530;

    /// <summary>
    /// Adds a Milvus container resource to the application model.
    /// </summary>
    /// <remarks>
    /// The .NET client library uses the gRPC port by default to communicate and this resource exposes that endpoint.
    /// A web-based administration tool for Milvus can also be added using <see cref="WithAttu"/>.
    /// This version of the package defaults to the <inheritdoc cref="MilvusContainerImageTags.Tag"/> tag of the <inheritdoc cref="MilvusContainerImageTags.Image"/> container image.
    /// <example>
    /// Use in application host
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var milvus = builder.AddMilvus("milvus");
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithReference(milvus);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency</param>
    /// <param name="apiKey">The parameter used to provide the auth key/token user for the Milvus resource.</param>
    /// <param name="grpcPort">The host port of gRPC endpoint of Milvus database.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{MilvusServerResource}"/>.</returns>
    [AspireExport("addMilvus", Description = "Adds a Milvus server resource to the distributed application model.")]
    public static IResourceBuilder<MilvusServerResource> AddMilvus(this IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<ParameterResource>? apiKey = null,
        int? grpcPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var apiKeyParameter = apiKey?.Resource ??
            ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-key");

        var milvus = new MilvusServerResource(name, apiKeyParameter);
        return builder.AddResource(milvus)
            .WithImage(MilvusContainerImageTags.Image, MilvusContainerImageTags.Tag)
            .WithImageRegistry(MilvusContainerImageTags.Registry)
            .WithHttpEndpoint(port: grpcPort, targetPort: MilvusPortGrpc, name: MilvusServerResource.PrimaryEndpointName)
            .WithEndpoint(MilvusServerResource.PrimaryEndpointName, endpoint =>
            {
                endpoint.Transport = "http2";
            })
            .WithEnvironment("COMMON_STORAGETYPE", "local")
            .WithEnvironment("ETCD_USE_EMBED", "true")
            .WithEnvironment("ETCD_DATA_DIR", "/var/lib/milvus/etcd")
            .WithEnvironment("COMMON_SECURITY_AUTHORIZATIONENABLED", "true")
            .WithEnvironment(ctx =>
            {
                ctx.EnvironmentVariables["COMMON_SECURITY_DEFAULTROOTPASSWORD"] = milvus.ApiKeyParameter;
            })
            .WithArgs("milvus", "run", "standalone");
    }

    /// <summary>
    /// Adds a Milvus database to the application model.
    /// </summary>
    /// <remarks>This method does not actually create the database in Milvus, rather helps complete a connection string that is used by the client component.
    /// <example>
    /// Use in application host
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var booksdb = builder.AddMilvus("milvus");
    ///   .AddDatabase("booksdb");
    ///
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithReference(booksdb);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    /// <param name="builder">The Milvus server resource builder.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addDatabase", Description = "Adds a Milvus database resource to a Milvus server resource.")]
    public static IResourceBuilder<MilvusDatabaseResource> AddDatabase(this IResourceBuilder<MilvusServerResource> builder, [ResourceName] string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        builder.Resource.AddDatabase(name, databaseName);
        var milvusDatabaseResource = new MilvusDatabaseResource(name, databaseName, builder.Resource);
        return builder.ApplicationBuilder.AddResource(milvusDatabaseResource);
    }

    /// <summary>
    /// Adds an administration and development platform for Milvus to the application model using Attu.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="MilvusContainerImageTags.AttuTag"/> tag of the <inheritdoc cref="MilvusContainerImageTags.AttuImage"/> container image.
    /// <example>
    /// Use in application host with a Milvus resource
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var milvus = builder.AddMilvus("milvus")
    ///   .WithAttu();
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithReference(milvus);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    /// <param name="builder">The Milvus server resource builder.</param>
    /// <param name="configureContainer">Configuration callback for Attu container resource.</param>
    /// <param name="containerName">The name of the container (Optional).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withAttu", Description = "Adds the Attu administration tool for Milvus.", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<T> WithAttu<T>(this IResourceBuilder<T> builder, Action<IResourceBuilder<AttuResource>>? configureContainer = null, string? containerName = null) where T : MilvusServerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        containerName ??= $"{builder.Resource.Name}-attu";

        var attuContainer = new AttuResource(containerName);
        var resourceBuilder = builder.ApplicationBuilder.AddResource(attuContainer)
                                                        .WithImage(MilvusContainerImageTags.AttuImage, MilvusContainerImageTags.AttuTag)
                                                        .WithImageRegistry(MilvusContainerImageTags.Registry)
                                                        .WithHttpEndpoint(targetPort: 3000, name: "http")
                                                        .WithEnvironment(context => ConfigureAttuContainer(context, builder.Resource))
                                                        .ExcludeFromManifest();

        configureContainer?.Invoke(resourceBuilder);

        return builder;
    }

    /// <summary>
    /// Adds a named volume for the data folder to a Milvus container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the resource name.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withDataVolume", Description = "Adds a persistent data volume to the Milvus server resource.")]
    public static IResourceBuilder<MilvusServerResource> WithDataVolume(this IResourceBuilder<MilvusServerResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/var/lib/milvus", isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a Milvus container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withDataBindMount", Description = "Mounts a host directory as the Milvus data directory.")]
    public static IResourceBuilder<MilvusServerResource> WithDataBindMount(this IResourceBuilder<MilvusServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/var/lib/milvus", isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the configuration of a Milvus container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configurationFilePath">The configuration file on the host to mount into the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete("Use WithConfigurationFile instead.")]
    public static IResourceBuilder<MilvusServerResource> WithConfigurationBindMount(this IResourceBuilder<MilvusServerResource> builder, string configurationFilePath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(configurationFilePath);

        return builder.WithBindMount(configurationFilePath, "/milvus/configs/milvus.yaml");
    }

    /// <summary>
    /// Copies a configuration file into a Milvus container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configurationFilePath">The configuration file on the host to copy into the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withConfigurationFile", Description = "Copies a Milvus configuration file into the container.")]
    public static IResourceBuilder<MilvusServerResource> WithConfigurationFile(this IResourceBuilder<MilvusServerResource> builder, string configurationFilePath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(configurationFilePath);

        return builder.WithContainerFiles("/milvus/configs", [
            new ContainerFile
            {
                Name = "milvus.yaml",
                SourcePath = configurationFilePath,
            },
        ]);
    }

    private static void ConfigureAttuContainer(EnvironmentCallbackContext context, MilvusServerResource resource)
    {
        // Attu assumes Milvus is being accessed over a default Aspire container network and hardcodes the resource address
        // This will need to be refactored once updated service discovery APIs are available
        context.EnvironmentVariables["MILVUS_URL"] = $"{resource.PrimaryEndpoint.Scheme}://{resource.Name}:{resource.PrimaryEndpoint.TargetPort}";
    }
}
