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

        var mongoDBContainer = new MongoDBServerResource(name, userName?.Resource, passwordParameter);

        string? connectionString = null;

        var healthCheckKey = $"{name}_check";
        // cache the client so it is reused on subsequent calls to the health check
        // IMongoClient? client = null;
        builder.Services.AddHealthChecks()
            .AddMyMongoDb(
                name: healthCheckKey,
                clientFactory: sp => new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is unavailable"))
            );

        return builder
            .AddResource(mongoDBContainer)
            .WithEndpoint(port: port, targetPort: DefaultContainerPort, name: MongoDBServerResource.PrimaryEndpointName, isProxied: false)
            .WithImage(MongoDBContainerImageTags.Image, MongoDBContainerImageTags.Tag)
            .WithImageRegistry(MongoDBContainerImageTags.Registry)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[UserEnvVarName] = mongoDBContainer.UserNameReference;
                context.EnvironmentVariables[PasswordEnvVarName] = mongoDBContainer.PasswordParameter!;
            })
            .OnConnectionStringAvailable(async (resource, @event, ct) =>
            {
                connectionString = await mongoDBContainer.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
                    ?? throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{mongoDBContainer.Name}' resource but the connection string was null.");
                if (resource.Name != "mongo2")
                {
                    return;
                }
                // await Task.Delay(2_000, ct).ConfigureAwait(false);
                // while (true)
                // {
                //     try
                //     {
                //         var client = new MongoClient(connectionString);
                //         var foo = await client.ListDatabaseNamesAsync(ct).ConfigureAwait(false);
                //     }
                //     catch (Exception ex)
                //     {
                //         Console.WriteLine(ex);
                //     }
                // }
            })
            .WithHealthCheck(healthCheckKey);
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
    /// This method will normally be called by the replica set resource builder when you add a MongoDB server resource as a member of the replica set using <see cref="MongoDBReplicaSetBuilderExtensions.WithMember(IResourceBuilder{MongoDBReplicaSetResource}, IResourceBuilder{MongoDBServerResource})"/>. It can also be called directly if are looking for lower-level control.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<MongoDBServerResource> WithReplicaSet(this IResourceBuilder<MongoDBServerResource> builder, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

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
    /// Configures the MongoDB server to use TLS with the specified certificate and CA files.
    /// </summary>
    /// <remarks>
    /// See https://www.mongodb.com/docs/manual/tutorial/configure-ssl/
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<MongoDBServerResource> WithTls(
        this IResourceBuilder<MongoDBServerResource> builder,
        MongoDBTlsMode mode = MongoDBTlsMode.RequireTls
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithAnnotation(new MongoDBServerTlsAnnotation(mode))
            .WithArgs("--tlsMode", mode switch
            {
                MongoDBTlsMode.Disabled => "disabled",
                MongoDBTlsMode.AllowTls => "allowTLS",
                MongoDBTlsMode.PreferTls => "preferTLS",
                MongoDBTlsMode.RequireTls => "requireTLS",
                _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Unsupported TLS mode: {mode}"),
            })
            .WithArgs("--tlsAllowConnectionsWithoutCertificates") // NOTE: This allows clients to connect without having to provide the certificate+key and the CA from their end (that's called mutual TLS and is unnecessary).
            .WithArgs("--tlsAllowInvalidCertificates") // TODO: Could be removed and replaced with `--tlsClusterFile <file>` (along with the more restrictive `--tlsAllowInvalidHostnames`) once Aspire adds support for TLS certificates with EKUs of `clientAuth` — see https://discord.com/channels/1361488941836140614/1361488942813286403/1516575977256259735
            .WithCertificateTrustConfiguration(async ctx =>
            {
                ctx.Arguments.Add("--tlsCAFile");
                ctx.Arguments.Add(ctx.CertificateBundlePath);
            })
            .WithHttpsCertificateConfiguration(async ctx =>
            {
                ctx.Arguments.Add("--tlsCertificateKeyFile");
                ctx.Arguments.Add(ctx.CertificateWithKeyPath);

                if (ctx.Password is not null)
                {
                    ctx.Arguments.Add("--tlsCertificateKeyFilePassword"); // NOTE: See https://www.mongodb.com/docs/manual/tutorial/configure-ssl/#tls-ssl-certificate-passphrase
                    ctx.Arguments.Add(ctx.Password);
                }
            });
    }

    private static void ConfigureMongoExpressContainer(EnvironmentCallbackContext context, MongoDBServerResource resource)
    {
        // Mongo Express assumes Mongo is being accessed over a default Aspire container network and hardcodes the resource address
        // This will need to be refactored once updated service discovery APIs are available
        context.EnvironmentVariables["ME_CONFIG_MONGODB_SERVER"] = resource.Name;
        var targetPort = resource.PrimaryEndpoint.TargetPort;
        if (targetPort is int targetPortValue)
        {
            context.EnvironmentVariables["ME_CONFIG_MONGODB_PORT"] = targetPortValue.ToString(CultureInfo.InvariantCulture);
        }
        context.EnvironmentVariables["ME_CONFIG_BASICAUTH"] = "false";
        if (resource.PasswordParameter is not null)
        {
            context.EnvironmentVariables["ME_CONFIG_MONGODB_ADMINUSERNAME"] = resource.UserNameReference;
            context.EnvironmentVariables["ME_CONFIG_MONGODB_ADMINPASSWORD"] = resource.PasswordParameter;
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
    /// The server does not use TLS.
    /// </summary>
    Disabled,

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
/// Represents the intent to configure a MongoDB server resource to use encrypted network transport via TLS.
/// </summary>
internal sealed record MongoDBServerTlsAnnotation(
    MongoDBTlsMode Mode
) : IResourceAnnotation;

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
