// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.MongoDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREDOCKERFILEBUILDER001

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding MongoDB resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class MongoDBBuilderExtensions
{
    // Internal port is always 27017.
    private const int DefaultContainerPort = 27017;

    private const string UserEnvVarName = "MONGO_INITDB_ROOT_USERNAME";
    private const string PasswordEnvVarName = "MONGO_INITDB_ROOT_PASSWORD";

    /// <summary>
    /// Adds a MongoDB resource to the application model. A container is used for local development.
    /// </summary>
    /// <remarks>
    /// <para>This version of the package defaults to the <inheritdoc cref="MongoDBContainerImageTags.Tag"/> tag of the <inheritdoc cref="MongoDBContainerImageTags.Image"/> container image.</para>
    /// <para>This overload is not available in polyglot app hosts. Use <see cref="AddMongoDB(IDistributedApplicationBuilder, string, int?, IResourceBuilder{ParameterResource}?, IResourceBuilder{ParameterResource}?)"/> instead.</para>
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for MongoDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with optional userName and password parameters instead.")]
    public static IResourceBuilder<MongoDBServerResource> AddMongoDB(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? port)
    {
        return AddMongoDB(builder, name, port, null, null);
    }

    /// <summary>
    /// <inheritdoc cref="AddMongoDB(IDistributedApplicationBuilder, string, int?)"/>
    /// </summary>
    /// <ats-summary>Adds a MongoDB container resource</ats-summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for MongoDB.</param>
    /// <param name="userName">A parameter that contains the MongoDb server user name, or <see langword="null"/> to use a default value.</param>
    /// <param name="password">A parameter that contains the MongoDb server password, or <see langword="null"/> to use a generated password.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<MongoDBServerResource> AddMongoDB(this IDistributedApplicationBuilder builder,
        string name,
        int? port = null,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", special: false);

        var mongoServerResource = new MongoDBServerResource(name, userName?.Resource, passwordParameter)
        {
            PasswordParameterWasGenerated = password is null,
        };

        string? connectionString = null;

        var healthCheckKey = $"{name}_check";
        // NOTE: `clientFactory` is invoked every time the healthcheck is performed. We cache the client so it is reused.
        var client = null as IMongoClient;
        builder.Services.AddHealthChecks()
            .AddMyMongoDb(
                name: healthCheckKey,
                clientFactory: sp => client ??= new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is unavailable")),
                // NOTE: Without a database as the target of the healthcheck, the healthcheck runs a `listDatabases` command against the Mongo server. This is problematic in cases where the Mongo server is a replica set secondary node, because during the phase in which the replica set is being initialized, the secondary node will return an error when `listDatabases` is called. To avoid this, we specify a database to use for the healthcheck. The healthcheck will then run a `ping` command against the specified database instead of `listDatabases`, which works even on a secondary node during replica set initialization.
                databaseNameFactory: _ => mongoServerResource.Databases.Values.FirstOrDefault(defaultValue: MongoDBServerResource.DefaultAuthenticationDatabase)
            );

        var mongoBuilder = builder
            .AddResource(mongoServerResource)
            .WithEndpoint(port: port, targetPort: DefaultContainerPort, name: MongoDBServerResource.PrimaryEndpointName)
            .WithImage(MongoDBContainerImageTags.Image, MongoDBContainerImageTags.Tag)
            .WithImageRegistry(MongoDBContainerImageTags.Registry)
            .WithIconName("DatabaseMultiple")
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[UserEnvVarName] = mongoServerResource.UserNameReference;
                context.EnvironmentVariables[PasswordEnvVarName] = mongoServerResource.PasswordParameter!;
            })
            .OnConnectionStringAvailable(async (resource, @event, ct) =>
            {
                connectionString = await resource.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
                    ?? throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{resource.Name}' resource but the connection string was null.");
            })
            .WithHealthCheck(healthCheckKey)
            .WithCertificateTrustConfiguration(context =>
            {
                // NOTE: `mongod` refuses to start when it is handed TLS file arguments without TLS actually being turned on, so these are only added once the endpoint has been marked as TLS-enabled.
                if (mongoServerResource.TlsEnabled)
                {
                    context.Arguments.Add("--tlsCAFile");
                    context.Arguments.Add(context.CertificateBundlePath);
                }

                return Task.CompletedTask;
            })
            .WithHttpsCertificateConfiguration(context =>
            {
                if (mongoServerResource.TlsEnabled)
                {
                    context.Arguments.Add("--tlsCertificateKeyFile");
                    context.Arguments.Add(context.CertificateWithKeyPath);

                    if (context.Password is not null)
                    {
                        context.Arguments.Add("--tlsCertificateKeyFilePassword"); // NOTE: See https://www.mongodb.com/docs/manual/tutorial/configure-ssl/#tls-ssl-certificate-passphrase
                        context.Arguments.Add(context.Password);
                    }
                }

                return Task.CompletedTask;
            });

        if (builder.ExecutionContext.IsRunMode)
        {
            mongoBuilder.SubscribeHttpsEndpointsUpdate(context =>
            {
                // NOTE: This callback also fires when the resource has explicitly asked for the developer certificate
                // (which is what `WithMember` does for replica set members) but no developer certificate exists on the
                // machine. Turning TLS on then would hand `mongod` a `--tlsMode` that it has no certificate to satisfy
                // and it would refuse to start, so the server is left serving plain TCP instead.
                var certificateIsAvailable =
                    (mongoServerResource.TryGetLastAnnotation<HttpsCertificateAnnotation>(out var certificateAnnotation) && certificateAnnotation.Certificate is not null)
                    || !context.Services.GetRequiredService<IDeveloperCertificateService>().Certificates.IsEmpty;

                if (!certificateIsAvailable)
                {
                    return;
                }

                // A certificate is available for this resource, so turn TLS on for the MongoDB endpoint. Marking the
                // endpoint itself is what makes `MongoDBServerResource.TlsEnabled` — and therefore the `tls=true`
                // segment of the connection string — light up.
                mongoBuilder
                    .WithEndpoint(MongoDBServerResource.PrimaryEndpointName, endpoint => endpoint.TlsEnabled = true)
                    .WithArgs(context =>
                    {
                        context.Args.Add("--tlsMode");
                        context.Args.Add(GetTlsModeArgument(mongoServerResource.TlsMode));
                        context.Args.Add("--tlsAllowConnectionsWithoutCertificates"); // NOTE: This allows clients to connect without having to provide the certificate+key and the CA from their end (that's called mutual TLS and is unnecessary).

                        if (mongoServerResource.TlsAllowInvalidCertificates)
                        {
                            context.Args.Add("--tlsAllowInvalidCertificates");
                        }
                    });
            });
        }

        return mongoBuilder;
    }

    /// <summary>
    /// Adds a MongoDB database to the application model.
    /// </summary>
    /// <param name="builder">The MongoDB server resource builder.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<MongoDBDatabaseResource> AddDatabase(this IResourceBuilder<MongoDBServerResource> builder, [ResourceName] string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        builder.Resource.AddDatabase(name, databaseName);
        var mongoDBDatabase = new MongoDBDatabaseResource(name, databaseName, builder.Resource);

        string? connectionString = null;

        builder.ApplicationBuilder.Eventing.Subscribe<ConnectionStringAvailableEvent>(mongoDBDatabase, async (@event, ct) =>
        {
            connectionString = await mongoDBDatabase.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

            if (connectionString == null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{mongoDBDatabase.Name}' resource but the connection string was null.");
            }
        });

        var healthCheckKey = $"{name}_check";
        // cache the database client so it is reused on subsequent calls to the health check
        IMongoDatabase? database = null;
        builder.ApplicationBuilder.Services.AddHealthChecks()
            .AddMongoDb(
                sp => database ??=
                    new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is unavailable"))
                        .GetDatabase(databaseName),
                name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(mongoDBDatabase)
            .WithIconName("Database")
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Adds a MongoExpress administration and development platform for MongoDB to the application model.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="MongoDBContainerImageTags.MongoExpressTag"/> tag of the <inheritdoc cref="MongoDBContainerImageTags.MongoExpressImage"/> container image.
    /// </remarks>
    /// <param name="builder">The MongoDB server resource builder.</param>
    /// <param name="configureContainer">Configuration callback for Mongo Express container resource.</param>
    /// <param name="containerName">The name of the container (Optional).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<T> WithMongoExpress<T>(this IResourceBuilder<T> builder, Action<IResourceBuilder<MongoExpressContainerResource>>? configureContainer = null, string? containerName = null)
        where T : MongoDBServerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        containerName ??= $"{builder.Resource.Name}-mongoexpress";

        var mongoExpressContainer = new MongoExpressContainerResource(containerName);
        var resourceBuilder = builder.ApplicationBuilder
            .AddResource(mongoExpressContainer)
            .WithImage(MongoDBContainerImageTags.MongoExpressImage, MongoDBContainerImageTags.MongoExpressTag)
            .WithImageRegistry(MongoDBContainerImageTags.MongoExpressRegistry)
            .WithIconName("WindowDatabase")
            .WithEnvironment(context => ConfigureMongoExpressContainer(context, builder.Resource))
            .WithHttpEndpoint(targetPort: 8081, name: "http")
            .WithParentRelationship(builder)
            .ExcludeFromManifest();

        configureContainer?.Invoke(resourceBuilder);

        return builder;
    }

    /// <summary>
    /// Configures the host port that the Mongo Express resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for Mongo Express.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<MongoExpressContainerResource> WithHostPort(this IResourceBuilder<MongoExpressContainerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Adds a named volume for the data folder to a MongoDB container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<MongoDBServerResource> WithDataVolume(this IResourceBuilder<MongoDBServerResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/data/db", isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a MongoDB container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<MongoDBServerResource> WithDataBindMount(this IResourceBuilder<MongoDBServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/data/db", isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the init folder to a MongoDB container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use <see cref="WithInitFiles"/> instead.</remarks>
    [Obsolete("Use WithInitFiles instead.")]
    [AspireExportIgnore(Reason = "Obsolete API. Use WithInitFiles instead.")]
    public static IResourceBuilder<MongoDBServerResource> WithInitBindMount(this IResourceBuilder<MongoDBServerResource> builder, string source, bool isReadOnly = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/docker-entrypoint-initdb.d", isReadOnly);
    }

    /// <summary>
    /// Copies init files into a MongoDB container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source file or directory on the host to copy into the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<MongoDBServerResource> WithInitFiles(this IResourceBuilder<MongoDBServerResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        const string initPath = "/docker-entrypoint-initdb.d";

        var importFullPath = Path.GetFullPath(source, builder.ApplicationBuilder.AppHostDirectory);

        return builder.WithContainerFiles(initPath, importFullPath);
    }

    /// <summary>
    /// Configures the MongoDB server to bind to and listen on all network interfaces.
    /// </summary>
    /// <remarks>
    /// See https://www.mongodb.com/docs/manual/reference/configuration-options/#mongodb-setting-net.bindIpAll
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<MongoDBServerResource> WithBindIpAll(this IResourceBuilder<MongoDBServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithAnnotation(new MongoDBServerBindAllIpAnnotation())
            .WithArgs("--bind_ip_all");
    }

    /// <summary>
    /// Annotates a MongoDB server resource as a member of a replica set with the specified name. This will configure the necessary command line arguments on the MongoDB container to initialize it as a member of the replica set.
    /// </summary>
    /// <remarks>
    /// This method will normally be called by the replica set resource builder when you add a MongoDB server resource as a member of the replica set using <see cref="MongoDBReplicaSetBuilderExtensions.WithMember(IResourceBuilder{MongoDBReplicaSetResource}, IResourceBuilder{MongoDBServerResource})"/>. It can also be called directly if you are looking for lower-level control.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<MongoDBServerResource> WithReplicaSet(this IResourceBuilder<MongoDBServerResource> builder, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ThrowIfPublishMode(builder.ApplicationBuilder, nameof(WithReplicaSet));

        builder.Resource.ReplicaSetName = name;
        return builder
            .WithAnnotation(new MongoDBServerReplicaSetAnnotation(name))
            .WithBindIpAll()
            .WithArgs("--replSet", name);
    }

    /// <summary>
    /// Sets up a keyfile for internal authentication between members of a MongoDB replica set, with the specified <paramref name="keyValue"/> as the content of the file.
    /// </summary>
    /// <remarks>
    /// The keyfile is a shared secret. Every member of the replica set (or sharded cluster) should have the same keyfile, and possession of that secret is what authenticates a connection as "a legitimate member of this cluster."
    /// See https://www.mongodb.com/docs/manual/tutorial/deploy-replica-set-with-keyfile-access-control/
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<MongoDBServerResource> WithKeyFile(
        this IResourceBuilder<MongoDBServerResource> builder,
        IExpressionValue keyValue,
        string keyFilePath = "/etc/rs.key"
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(keyValue);
        ArgumentException.ThrowIfNullOrEmpty(keyFilePath);
        // NOTE: The keyfile is a shared secret. Publishers materialize container files into the publish artifact (e.g. Docker Compose writes the contents straight into the generated YAML), which would leak it, so publishing is rejected outright until the keyfile can be published as a secret.
        ThrowIfPublishMode(builder.ApplicationBuilder, nameof(WithKeyFile));

        return builder
            .WithAnnotation(new MongoDBServerKeyFileAnnotation(keyValue, keyFilePath))
            .WithContainerFiles(
                destinationPath: Path.GetDirectoryName(keyFilePath)!,
                callback: async (_, ct) => [new ContainerFile
                {
                    Name = Path.GetFileName(keyFilePath),
                    Contents = await keyValue.GetValueAsync(ct).ConfigureAwait(false),
                    Mode = UnixFileMode.UserRead,
                }],
                // NOTE: 999 is the default user and group id used by the official MongoDB container image for the mongod process
                defaultOwner: 999,
                defaultGroup: 999
            )
            .WithArgs("--keyFile", keyFilePath);
    }

    /// <summary>
    /// Configures the <c>--tlsMode</c> the MongoDB server is started with when TLS is active.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TLS itself is not turned on by this method. A MongoDB server resource serves TLS whenever an HTTPS/TLS certificate
    /// is available for it — by default the ASP.NET Core developer certificate — and serves plain TCP otherwise. Use
    /// <see cref="ResourceBuilderExtensions.WithHttpsDeveloperCertificate{TResource}"/> or
    /// <see cref="ResourceBuilderExtensions.WithHttpsCertificate{TResource}"/> to opt in explicitly, and
    /// <see cref="ResourceBuilderExtensions.WithoutHttpsCertificate{TResource}"/> to opt out. This method only controls
    /// how strict the server is about TLS on incoming connections once TLS is active; the default is
    /// <see cref="MongoDBTlsMode.RequireTls"/>.
    /// </para>
    /// <para>
    /// See https://www.mongodb.com/docs/manual/reference/configuration-options/#mongodb-setting-net.tls.mode
    /// </para>
    /// <example>
    /// Let clients connect either with or without TLS:
    /// <code lang="csharp">
    /// var mongo = builder.AddMongoDB("mongo")
    ///     .WithTlsMode(MongoDBTlsMode.PreferTls);
    /// </code>
    /// </example>
    /// </remarks>
    /// <param name="builder">The MongoDB server resource builder.</param>
    /// <param name="mode">The TLS mode to run the MongoDB server in.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<MongoDBServerResource> WithTlsMode(
        this IResourceBuilder<MongoDBServerResource> builder,
        MongoDBTlsMode mode = MongoDBTlsMode.RequireTls
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Unsupported TLS mode: {mode}");
        }
        // NOTE: The certificate material is only made available to the container in run mode, so a published container configured for TLS would be started without a certificate and fail. Publishing is rejected until publish-time certificate support exists.
        ThrowIfPublishMode(builder.ApplicationBuilder, nameof(WithTlsMode));

        return builder.WithAnnotation(new MongoDBServerTlsModeAnnotation(mode), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Configures the MongoDB server to accept TLS connections whose peer certificate cannot be validated, by passing
    /// <c>--tlsAllowInvalidCertificates</c> to <c>mongod</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This weakens certificate authentication and should only be used when a peer legitimately cannot present a
    /// certificate this server is able to validate. It is what
    /// <see cref="MongoDBReplicaSetBuilderExtensions.WithMember(IResourceBuilder{MongoDBReplicaSetResource}, IResourceBuilder{MongoDBServerResource})"/>
    /// uses today, because replica set members authenticate to each other with the same certificate they serve to clients
    /// and that certificate does not carry a <c>clientAuth</c> extended key usage.
    /// </para>
    /// <para>
    /// See https://www.mongodb.com/docs/manual/reference/program/mongod/#std-option-mongod.--tlsAllowInvalidCertificates
    /// </para>
    /// <example>
    /// <code lang="csharp">
    /// var mongo = builder.AddMongoDB("mongo")
    ///     .WithTlsAllowInvalidCertificates();
    /// </code>
    /// </example>
    /// </remarks>
    /// <param name="builder">The MongoDB server resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<MongoDBServerResource> WithTlsAllowInvalidCertificates(this IResourceBuilder<MongoDBServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ThrowIfPublishMode(builder.ApplicationBuilder, nameof(WithTlsAllowInvalidCertificates));

        return builder.WithAnnotation(new MongoDBServerTlsAllowInvalidCertificatesAnnotation(), ResourceAnnotationMutationBehavior.Replace);
    }

    private static string GetTlsModeArgument(MongoDBTlsMode mode) => mode switch
    {
        MongoDBTlsMode.AllowTls => "allowTLS",
        MongoDBTlsMode.PreferTls => "preferTLS",
        MongoDBTlsMode.RequireTls => "requireTLS",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Unsupported TLS mode: {mode}"),
    };

    /// <summary>
    /// Throws when the app host is running in publish mode, for the features of this integration that are only
    /// implemented for local orchestration.
    /// </summary>
    internal static void ThrowIfPublishMode(IDistributedApplicationBuilder builder, string feature)
    {
        if (builder.ExecutionContext.IsPublishMode)
        {
            throw new NotSupportedException($"'{feature}' is not supported when publishing or deploying the application. Publish support for this MongoDB feature has not been implemented yet; it can only be used when running the app host locally.");
        }
    }

    private static void ConfigureMongoExpressContainer(EnvironmentCallbackContext context, MongoDBServerResource resource)
    {
        // Mongo Express assumes Mongo is being accessed over a default Aspire container network and hardcodes the resource address
        // This will need to be refactored once updated service discovery APIs are available
        context.EnvironmentVariables["ME_CONFIG_MONGODB_SERVER"] = resource.Name;
        var targetPort = resource.PrimaryEndpoint.TargetPort;
        if (targetPort is int targetPortValue)
        {
            var port = targetPortValue.ToString(CultureInfo.InvariantCulture);
            context.EnvironmentVariables["ME_CONFIG_MONGODB_PORT"] = port;
            // NOTE: Before starting Mongo Express, the image's entrypoint waits for the server to accept connections, and it
            // takes the address to wait on from `ME_CONFIG_MONGODB_URL` alone — the image ships a default of
            // `mongodb://mongo:27017`. Leaving that default in place makes every container spend its whole retry budget
            // waiting on a host that does not exist before it even starts.
            // NOTE: This variable does not need to carry credentials or TLS options, because Mongo Express itself ignores
            // it: it only reads `ME_CONFIG_MONGODB_URL` when `ME_CONFIG_MONGODB_SERVER` is unset, which it is not here.
            context.EnvironmentVariables["ME_CONFIG_MONGODB_URL"] = $"mongodb://{resource.Name}:{port}";
        }
        context.EnvironmentVariables["ME_CONFIG_BASICAUTH"] = "false";
        if (resource.PasswordParameter is not null)
        {
            context.EnvironmentVariables["ME_CONFIG_MONGODB_ADMINUSERNAME"] = resource.UserNameReference;
            context.EnvironmentVariables["ME_CONFIG_MONGODB_ADMINPASSWORD"] = resource.PasswordParameter;
        }

        if (resource.TlsEnabled)
        {
            // NOTE: The server only accepts TLS connections, and Mongo Express defaults to plain TCP, so it has to be told
            // to speak TLS as well or it cannot connect at all.
            context.EnvironmentVariables["ME_CONFIG_MONGODB_SSL"] = "true";
            // NOTE: Mongo Express reaches the server at its resource name on the container network, which is not a name that
            // any certificate Aspire can issue for the server will carry, and it exposes no way to keep chain validation
            // while relaxing only the host name check. This mirrors the relaxation that replica set members need for the
            // connections they make to each other.
            context.EnvironmentVariables["ME_CONFIG_MONGODB_SSLVALIDATE"] = "false";
        }
    }
}

/// <summary>
/// Defines the TLS mode for MongoDB server.
/// </summary>
/// <remarks>
/// See https://www.mongodb.com/docs/manual/reference/configuration-options/#mongodb-setting-net.tls.mode
/// </remarks>
public enum MongoDBTlsMode
{
    /// <summary>
    /// Connections between servers do not use TLS. For incoming connections, the server accepts both TLS and non-TLS.
    /// </summary>
    AllowTls,

    /// <summary>
    /// Connections between servers use TLS. For incoming connections, the server accepts both TLS and non-TLS.
    /// </summary>
    PreferTls,

    /// <summary>
    /// The server uses and accepts only TLS encrypted connections.
    /// </summary>
    RequireTls,
}

/// <summary>
/// Represents the intent to configure a MongoDB server resource to bind to and listen on all network interfaces.
/// </summary>
internal sealed record MongoDBServerBindAllIpAnnotation : IResourceAnnotation;

/// <summary>
/// Represents the intent to configure a MongoDB server resource as a member of a replica set with the specified name.
/// </summary>
internal sealed record MongoDBServerReplicaSetAnnotation(
    string Name
) : IResourceAnnotation;

/// <summary>
/// Represents the intent to configure a MongoDB server resource with a keyfile for internal authentication between members of a replica set, with the specified <paramref name="Value"/> as the content of the keyfile and the specified <paramref name="FilePath"/> as the path to the keyfile in the container.
/// </summary>
internal sealed record MongoDBServerKeyFileAnnotation(
    IExpressionValue Value,
    string FilePath
) : IResourceAnnotation;

/// <summary>
/// Represents the intent to run a MongoDB server resource in a specific TLS mode when TLS is active.
/// </summary>
internal sealed record MongoDBServerTlsModeAnnotation(
    MongoDBTlsMode Mode
) : IResourceAnnotation;

/// <summary>
/// Represents the intent to configure a MongoDB server resource to accept TLS connections whose peer certificate cannot be validated.
/// </summary>
internal sealed record MongoDBServerTlsAllowInvalidCertificatesAnnotation : IResourceAnnotation;

internal static class MyMongoDbHealthCheckBuilderExtensions
{
    private const string NAME = "mongodb";

    public static IHealthChecksBuilder AddMyMongoDb(
        this IHealthChecksBuilder builder,
        Func<IServiceProvider, IMongoClient>? clientFactory = default,
        Func<IServiceProvider, string>? databaseNameFactory = default,
        string? name = default,
        HealthStatus? failureStatus = default,
        IEnumerable<string>? tags = default,
        TimeSpan? timeout = default)
    {
        return builder.Add(new HealthCheckRegistration(
            name ?? NAME,
            sp => Factory(sp, clientFactory, databaseNameFactory),
            failureStatus,
            tags,
            timeout));

        static MyMongoDbHealthCheck Factory(IServiceProvider sp, Func<IServiceProvider, IMongoClient>? clientFactory, Func<IServiceProvider, string>? databaseNameFactory)
        {
            // The user might have registered a factory for MongoClient type, but not for the abstraction (IMongoClient).
            // That is why we try to resolve MongoClient first.
            IMongoClient client = clientFactory?.Invoke(sp) ?? sp.GetService<MongoClient>() ?? sp.GetRequiredService<IMongoClient>();
            string? databaseName = databaseNameFactory?.Invoke(sp);
            return new(client, databaseName);
        }
    }
}
internal class MyMongoDbHealthCheck : IHealthCheck
{
    // When running the tests locally during development, don't re-attempt
    // as it prolongs the time it takes to run the tests.
    private const int MAX_PING_ATTEMPTS
#if DEBUG
        = 1;
#else
        = 2;
#endif

    private static readonly Lazy<BsonDocumentCommand<BsonDocument>> s_command = new(() => new(BsonDocument.Parse("{ping:1}")));
    private readonly IMongoClient _client;
    private readonly string? _specifiedDatabase;

    public MyMongoDbHealthCheck(IMongoClient client, string? databaseName = default)
    {
        _client = client;
        _specifiedDatabase = databaseName;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(_specifiedDatabase))
            {
                // some users can't list all databases depending on database privileges, with
                // this you can check a specified database.
                // Related with issue #43 and #617

                // For most operations where it is possible, the MongoDB driver itself will retry exactly once
                // to cover switches in the primary and temporary short term network outages.
                // Due to the RunCommand being a lower level function, according to the spec (https://github.com/mongodb/specifications/blob/master/source/run-command/run-command.rst#retryability)
                // for it, it is not retryable and this extends to the ping.
                for (int attempt = 1; attempt <= MAX_PING_ATTEMPTS; attempt++)

                {
                    try
                    {
                        await _client
                            .GetDatabase(_specifiedDatabase)
                            .RunCommandAsync(s_command.Value, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        if (MAX_PING_ATTEMPTS == attempt)
                        {
                            throw;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }
            else
            {
                using var cursor = await _client.ListDatabaseNamesAsync(cancellationToken).ConfigureAwait(false);
                await cursor.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
