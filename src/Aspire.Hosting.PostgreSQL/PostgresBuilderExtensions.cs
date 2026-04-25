// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREMCP001

using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding PostgreSQL resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class PostgresBuilderExtensions
{
    private const string UserEnvVarName = "POSTGRES_USER";
    private const string PasswordEnvVarName = "POSTGRES_PASSWORD";
    private const string PostgresMcpDatabaseUriEnvVarName = "DATABASE_URI";

    /// <summary>
    /// Adds a PostgreSQL resource to the application model. A container is used for local development.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="userName">The parameter used to provide the user name for the PostgreSQL resource. If <see langword="null"/> a default value will be used.</param>
    /// <param name="password">The parameter used to provide the administrator password for the PostgreSQL resource. If <see langword="null"/> a random password will be generated.</param>
    /// <param name="port">The host port used when launching the container. If null a random port will be assigned.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This resource includes built-in health checks. When this resource is referenced as a dependency
    /// using the <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// extension method then the dependent resource will wait until the Postgres resource is able to service
    /// requests.
    /// </para>
    /// This version of the package defaults to the <inheritdoc cref="PostgresContainerImageTags.Tag"/> tag of the <inheritdoc cref="PostgresContainerImageTags.Image"/> container image.
    /// </remarks>
    [AspireExport(Description = "Adds a PostgreSQL server resource")]
    public static IResourceBuilder<PostgresServerResource> AddPostgres(this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password");

        var postgresServer = new PostgresServerResource(name, userName?.Resource, passwordParameter);

        string? connectionString = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(postgresServer, async (@event, ct) =>
        {
            connectionString = await postgresServer.GetConnectionStringAsync(ct).ConfigureAwait(false);

            if (connectionString == null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{postgresServer.Name}' resource but the connection string was null.");
            }
        });

        builder.Eventing.Subscribe<ResourceReadyEvent>(postgresServer, async (@event, ct) =>
        {
            if (connectionString is null)
            {
                throw new DistributedApplicationException($"ResourceReadyEvent was published for the '{postgresServer.Name}' resource but the connection string was null.");
            }

            // Non-database scoped connection string
            using var npgsqlConnection = new NpgsqlConnection(connectionString + ";Database=postgres;");

            await npgsqlConnection.OpenAsync(ct).ConfigureAwait(false);

            if (npgsqlConnection.State != System.Data.ConnectionState.Open)
            {
                throw new InvalidOperationException($"Could not open connection to '{postgresServer.Name}'");
            }

            foreach (var name in postgresServer.Databases.Keys)
            {
                if (builder.Resources.FirstOrDefault(n => string.Equals(n.Name, name, StringComparisons.ResourceName)) is PostgresDatabaseResource postgreDatabase)
                {
                    await CreateDatabaseAsync(npgsqlConnection, postgreDatabase, @event.Services, ct).ConfigureAwait(false);
                }
            }
        });

        var healthCheckKey = $"{name}_check";
        builder.Services.AddHealthChecks().AddNpgSql(sp => connectionString ?? throw new InvalidOperationException("Connection string is unavailable"), name: healthCheckKey, configure: (connection) =>
        {
            // HACK: The Npgsql client defaults to using the username in the connection string if the database is not specified. Here
            //       we override this default behavior because we are working with a non-database scoped connection string. The Aspirified
            //       package doesn't have to deal with this because it uses a datasource from DI which doesn't have this issue:
            //
            //       https://github.com/npgsql/npgsql/blob/c3b31c393de66a4b03fba0d45708d46a2acb06d2/src/Npgsql/NpgsqlConnection.cs#L445
            //
            connection.ConnectionString += ";Database=postgres;";
        });

        return builder.AddResource(postgresServer)
                      .WithEndpoint(port: port, targetPort: 5432, name: PostgresServerResource.PrimaryEndpointName) // Internal port is always 5432.
                      .WithImage(PostgresContainerImageTags.Image, PostgresContainerImageTags.Tag)
                      .WithImageRegistry(PostgresContainerImageTags.Registry)
                      .WithIconName("DatabaseMultiple")
                      .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "scram-sha-256")
                      .WithEnvironment("POSTGRES_INITDB_ARGS", "--auth-host=scram-sha-256 --auth-local=scram-sha-256")
                      .WithEnvironment(context =>
                      {
                          context.EnvironmentVariables[UserEnvVarName] = postgresServer.UserNameReference;
                          context.EnvironmentVariables[PasswordEnvVarName] = postgresServer.PasswordParameter;
                      })
                      .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Adds a PostgreSQL database to the application model.
    /// </summary>
    /// <param name="builder">The PostgreSQL server resource builder.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This resource includes built-in health checks. When this resource is referenced as a dependency
    /// using the <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// extension method then the dependent resource will wait until the Postgres database is available.
    /// </para>
    /// <para>
    /// Note that calling <see cref="AddDatabase(IResourceBuilder{PostgresServerResource}, string, string?)"/>
    /// will result in the database being created on the Postgres server when the server becomes ready.
    /// The database creation happens automatically as part of the resource lifecycle.
    /// </para>
    /// </remarks>
    [AspireExport(Description = "Adds a PostgreSQL database")]
    public static IResourceBuilder<PostgresDatabaseResource> AddDatabase(this IResourceBuilder<PostgresServerResource> builder, [ResourceName] string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        var postgresDatabase = new PostgresDatabaseResource(name, databaseName, builder.Resource);

        builder.Resource.AddDatabase(postgresDatabase.Name, postgresDatabase.DatabaseName);

        string? connectionString = null;

        builder.ApplicationBuilder.Eventing.Subscribe<ConnectionStringAvailableEvent>(postgresDatabase, async (@event, ct) =>
        {
            connectionString = await postgresDatabase.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

            if (connectionString == null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{name}' resource but the connection string was null.");
            }
        });

        var healthCheckKey = $"{name}_check";
        builder.ApplicationBuilder.Services.AddHealthChecks().AddNpgSql(sp => connectionString ?? throw new InvalidOperationException("Connection string is unavailable"), name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(postgresDatabase)
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Adds a pgAdmin 4 administration and development platform for PostgreSQL to the application model.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="PostgresContainerImageTags.PgAdminTag"/> tag of the <inheritdoc cref="PostgresContainerImageTags.PgAdminImage"/> container image.
    /// </remarks>
    /// <param name="builder">The PostgreSQL server resource builder.</param>
    /// <param name="configureContainer">Callback to configure PgAdmin container resource.</param>
    /// <param name="containerName">The name of the container (Optional).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds pgAdmin 4 management UI", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<T> WithPgAdmin<T>(this IResourceBuilder<T> builder, Action<IResourceBuilder<PgAdminContainerResource>>? configureContainer = null, string? containerName = null)
        where T : PostgresServerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.Resources.OfType<PgAdminContainerResource>().SingleOrDefault() is { } existingPgAdminResource)
        {
            var builderForExistingResource = builder.ApplicationBuilder.CreateResourceBuilder(existingPgAdminResource);
            configureContainer?.Invoke(builderForExistingResource);
            return builder;
        }
        else
        {
            containerName ??= "pgadmin";

            var pgAdminContainer = new PgAdminContainerResource(containerName);
            var pgAdminContainerBuilder = builder.ApplicationBuilder.AddResource(pgAdminContainer)
                                                 .WithImage(PostgresContainerImageTags.PgAdminImage, PostgresContainerImageTags.PgAdminTag)
                                                 .WithImageRegistry(PostgresContainerImageTags.PgAdminRegistry)
                                                 .WithHttpEndpoint(targetPort: 80, name: "http")
                                                 .WithEnvironment(SetPgAdminEnvironmentVariables)
                                                 .WithHttpHealthCheck("/browser")
                                                 .ExcludeFromManifest();

            pgAdminContainerBuilder.WithContainerFiles(
                destinationPath: "/pgadmin4",
                callback: async (context, cancellationToken) =>
                {
                    var appModel = context.ServiceProvider.GetRequiredService<DistributedApplicationModel>();
                    var postgresInstances = builder.ApplicationBuilder.Resources.OfType<PostgresServerResource>();

                    return [
                        new ContainerFile
                        {
                            Name = "servers.json",
                            Contents = await WritePgAdminServerJson(postgresInstances, cancellationToken).ConfigureAwait(false),
                        },
                    ];
                });

            configureContainer?.Invoke(pgAdminContainerBuilder);

            pgAdminContainerBuilder.WithRelationship(builder.Resource, "PgAdmin");

            return builder;
        }
    }

    /// <summary>
    /// Configures the host port that the PGAdmin resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for PGAdmin.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>The resource builder for PGAdmin.</returns>
    [AspireExport("withPgAdminHostPort", MethodName = "withHostPort", Description = "Sets the host port for pgAdmin")]
    public static IResourceBuilder<PgAdminContainerResource> WithHostPort(this IResourceBuilder<PgAdminContainerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Configures the host port that the pgweb resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for pgweb.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>The resource builder for pgweb.</returns>
    [AspireExport("withPgWebHostPort", MethodName = "withHostPort", Description = "Sets the host port for pgweb")]
    public static IResourceBuilder<PgWebContainerResource> WithHostPort(this IResourceBuilder<PgWebContainerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Adds an administration and development platform for PostgreSQL to the application model using pgweb.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="PostgresContainerImageTags.PgWebTag"/> tag of the <inheritdoc cref="PostgresContainerImageTags.PgWebImage"/> container image.
    /// </remarks>
    /// <param name="builder">The Postgres server resource builder.</param>
    /// <param name="configureContainer">Configuration callback for pgweb container resource.</param>
    /// <param name="containerName">The name of the container (Optional).</param>
    /// <remarks>
    /// <example>
    /// Use in application host with a Postgres resource
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var postgres = builder.AddPostgres("postgres")
    ///    .WithPgWeb();
    /// var db = postgres.AddDatabase("db");
    ///
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithReference(db);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds pgweb management UI", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<PostgresServerResource> WithPgWeb(this IResourceBuilder<PostgresServerResource> builder, Action<IResourceBuilder<PgWebContainerResource>>? configureContainer = null, string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.Resources.OfType<PgWebContainerResource>().SingleOrDefault() is { } existingPgWebResource)
        {
            var builderForExistingResource = builder.ApplicationBuilder.CreateResourceBuilder(existingPgWebResource);
            configureContainer?.Invoke(builderForExistingResource);
            return builder;
        }
        else
        {
            containerName ??= "pgweb";

            var pgwebContainer = new PgWebContainerResource(containerName);
            var pgwebContainerBuilder = builder.ApplicationBuilder.AddResource(pgwebContainer)
                                               .WithImage(PostgresContainerImageTags.PgWebImage, PostgresContainerImageTags.PgWebTag)
                                               .WithImageRegistry(PostgresContainerImageTags.PgWebRegistry)
                                               .WithHttpEndpoint(targetPort: 8081, name: "http")
                                               .WithArgs("--bookmarks-dir=/.pgweb/bookmarks")
                                               .WithArgs("--sessions")
                                               .ExcludeFromManifest();

            configureContainer?.Invoke(pgwebContainerBuilder);

            pgwebContainerBuilder.WithRelationship(builder.Resource, "PgWeb");

            pgwebContainerBuilder.WithHttpHealthCheck();

            pgwebContainerBuilder.WithContainerFiles(
                destinationPath: "/",
                callback: async (context, ct) =>
                {
                    var appModel = context.ServiceProvider.GetRequiredService<DistributedApplicationModel>();
                    var postgresInstances = builder.ApplicationBuilder.Resources.OfType<PostgresDatabaseResource>();

                    // Add the bookmarks to the pgweb container
                    return [
                        new ContainerDirectory
                        {
                            Name = ".pgweb",
                            Entries = [
                                new ContainerDirectory
                                {
                                    Name = "bookmarks",
                                    Entries = await WritePgWebBookmarks(postgresInstances, ct).ConfigureAwait(false)
                                },
                            ],
                        },
                    ];
                });

            return builder;
        }
    }

    /// <summary>
    /// Adds a Postgres MCP server container and configures it to connect to the database represented by <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The Postgres database resource builder.</param>
    /// <param name="configureContainer">Configuration callback for the Postgres MCP container resource.</param>
    /// <param name="containerName">The name of the container (optional).</param>
    /// <remarks>
    /// The Postgres MCP server is configured to use SSE transport and will expose an HTTP endpoint.
    /// This version of the package defaults to the <inheritdoc cref="PostgresContainerImageTags.PostgresMcpTag"/> tag of the <inheritdoc cref="PostgresContainerImageTags.PostgresMcpImage"/> container image.
    /// </remarks>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds Postgres MCP server", RunSyncOnBackgroundThread = true)]
    [Experimental("ASPIREPOSTGRES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<PostgresDatabaseResource> WithPostgresMcp(
        this IResourceBuilder<PostgresDatabaseResource> builder,
        Action<IResourceBuilder<PostgresMcpContainerResource>>? configureContainer = null,
        string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        containerName ??= $"{builder.Resource.Name}-mcp";

        if (builder.ApplicationBuilder.Resources.TryGetByName(containerName, out var existingResource) && existingResource is PostgresMcpContainerResource existing)
        {
            var existingBuilder = builder.ApplicationBuilder.CreateResourceBuilder(existing);
            configureContainer?.Invoke(existingBuilder);
            return builder;
        }

        var mcpContainer = new PostgresMcpContainerResource(containerName);
        var mcpContainerBuilder = builder.ApplicationBuilder.AddResource(mcpContainer)
            .WithImage(PostgresContainerImageTags.PostgresMcpImage, PostgresContainerImageTags.PostgresMcpTag)
            .WithImageRegistry(PostgresContainerImageTags.PostgresMcpRegistry)
            .WithHttpEndpoint(targetPort: 8000, name: PostgresMcpContainerResource.PrimaryEndpointName)
            .WithArgs("--access-mode=unrestricted")
            .WithArgs("--transport=sse")
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[PostgresMcpDatabaseUriEnvVarName] = builder.Resource.UriExpression;
            })
            .WithMcpServer("/sse", endpointName: PostgresMcpContainerResource.PrimaryEndpointName)
            .WithIconName("BrainCircuit") // Show a BrainCircuit icon for MCP resources in the dashboard
            .WaitFor(builder);

        configureContainer?.Invoke(mcpContainerBuilder);

        mcpContainerBuilder.WithParentRelationship(builder.Resource);

        return builder;
    }

    private static void SetPgAdminEnvironmentVariables(EnvironmentCallbackContext context)
    {
        // Disables pgAdmin authentication.
        context.EnvironmentVariables["PGADMIN_CONFIG_MASTER_PASSWORD_REQUIRED"] = "False";
        context.EnvironmentVariables["PGADMIN_CONFIG_SERVER_MODE"] = "False";

        // You need to define the PGADMIN_DEFAULT_EMAIL and PGADMIN_DEFAULT_PASSWORD or PGADMIN_DEFAULT_PASSWORD_FILE environment variables.
        context.EnvironmentVariables["PGADMIN_DEFAULT_EMAIL"] = "admin@domain.com";
        context.EnvironmentVariables["PGADMIN_DEFAULT_PASSWORD"] = "admin";

        // When running in the context of Codespaces we need to set some additional environment
        // variables so that PGAdmin will trust the forwarded headers that Codespaces port
        // forwarding will send.
        var config = context.ExecutionContext.ServiceProvider.GetRequiredService<IConfiguration>();
        if (context.ExecutionContext.IsRunMode && config.GetValue<bool>("CODESPACES", false))
        {
            context.EnvironmentVariables["PGADMIN_CONFIG_PROXY_X_HOST_COUNT"] = "1";
            context.EnvironmentVariables["PGADMIN_CONFIG_PROXY_X_PREFIX_COUNT"] = "1";
        }
    }

    /// <summary>
    /// Adds a named volume for the data folder to a PostgreSQL container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The data directory location varies by PostgreSQL version:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>PostgreSQL 17 and earlier: <c>/var/lib/postgresql/data</c></description></item>
    ///   <item><description>PostgreSQL 18 and later: <c>/var/lib/postgresql</c></description></item>
    /// </list>
    /// <para>
    /// This method automatically selects the correct path based on the configured container image tag.
    /// </para>
    /// </remarks>
    [AspireExport(Description = "Adds a data volume for PostgreSQL")]
    public static IResourceBuilder<PostgresServerResource> WithDataVolume(this IResourceBuilder<PostgresServerResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var dataPath = GetPostgresDataDirectoryPath(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"),
            dataPath, isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a PostgreSQL container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The data directory location varies by PostgreSQL version:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>PostgreSQL 17 and earlier: <c>/var/lib/postgresql/data</c></description></item>
    ///   <item><description>PostgreSQL 18 and later: <c>/var/lib/postgresql</c></description></item>
    /// </list>
    /// <para>
    /// This method automatically selects the correct path based on the configured container image tag.
    /// </para>
    /// </remarks>
    [AspireExport(Description = "Adds a data bind mount for PostgreSQL")]
    public static IResourceBuilder<PostgresServerResource> WithDataBindMount(this IResourceBuilder<PostgresServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        var dataPath = GetPostgresDataDirectoryPath(builder);

        return builder.WithBindMount(source, dataPath, isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the init folder to a PostgreSQL container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use <see cref="WithInitFiles"/> instead.</remarks>
    [AspireExportIgnore(Reason = "Obsolete. Use WithInitFiles instead.")]
    [Obsolete("Use WithInitFiles instead.")]
    public static IResourceBuilder<PostgresServerResource> WithInitBindMount(this IResourceBuilder<PostgresServerResource> builder, string source, bool isReadOnly = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/docker-entrypoint-initdb.d", isReadOnly);
    }

    /// <summary>
    /// Copies init files to a PostgreSQL container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory or files on the host to copy into the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Copies init files to PostgreSQL")]
    public static IResourceBuilder<PostgresServerResource> WithInitFiles(this IResourceBuilder<PostgresServerResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        const string initPath = "/docker-entrypoint-initdb.d";

        var importFullPath = Path.GetFullPath(source, builder.ApplicationBuilder.AppHostDirectory);

        return builder.WithContainerFiles(initPath, importFullPath);
    }

    /// <summary>
    /// Defines the SQL script used to create the database.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="PostgresDatabaseResource"/>.</param>
    /// <param name="script">The SQL script used to create the database.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The script can only contain SQL statements applying to the default database like CREATE DATABASE. Custom statements like table creation
    /// and data insertion are not supported since they require a distinct connection to the newly created database.
    /// <value>Default script is <code>CREATE DATABASE "&lt;QUOTED_DATABASE_NAME&gt;"</code></value>
    /// </remarks>
    [AspireExport(Description = "Defines the SQL script for database creation")]
    public static IResourceBuilder<PostgresDatabaseResource> WithCreationScript(this IResourceBuilder<PostgresDatabaseResource> builder, string script)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(script);

        builder.WithAnnotation(new PostgresCreateDatabaseScriptAnnotation(script));

        return builder;
    }

    /// <summary>
    /// Configures the password that the PostgreSQL resource is used.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="password">The parameter used to provide the password for the PostgreSQL resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Configures the PostgreSQL password")]
    public static IResourceBuilder<PostgresServerResource> WithPassword(this IResourceBuilder<PostgresServerResource> builder, IResourceBuilder<ParameterResource> password)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(password);

        builder.Resource.PasswordParameter = password.Resource;
        return builder;
    }

    /// <summary>
    /// Configures the user name that the PostgreSQL resource is used.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="userName">The parameter used to provide the user name for the PostgreSQL resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Configures the PostgreSQL user name")]
    public static IResourceBuilder<PostgresServerResource> WithUserName(this IResourceBuilder<PostgresServerResource> builder, IResourceBuilder<ParameterResource> userName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(userName);

        builder.Resource.UserNameParameter = userName.Resource;
        return builder;
    }

    /// <summary>
    /// Configures the host port that the PostgreSQL resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withPostgresHostPort", MethodName = "withHostPort", Description = "Sets the host port for PostgreSQL")]
    public static IResourceBuilder<PostgresServerResource> WithHostPort(this IResourceBuilder<PostgresServerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEndpoint(PostgresServerResource.PrimaryEndpointName, endpoint =>
        {
            endpoint.Port = port;
        });
    }

    private static async Task<IEnumerable<ContainerFileSystemItem>> WritePgWebBookmarks(IEnumerable<PostgresDatabaseResource> postgresInstances, CancellationToken cancellationToken)
    {
        var bookmarkFiles = new List<ContainerFileSystemItem>();

        foreach (var postgresDatabase in postgresInstances)
        {
            var user = postgresDatabase.Parent.UserNameParameter is null
            ? "postgres"
            : await postgresDatabase.Parent.UserNameParameter.GetValueAsync(cancellationToken).ConfigureAwait(false);

            var password = await postgresDatabase.Parent.PasswordParameter.GetValueAsync(cancellationToken).ConfigureAwait(false) ?? "password";

            // PgAdmin assumes Postgres is being accessed over a default Aspire container network and hardcodes the resource address
            // This will need to be refactored once updated service discovery APIs are available
            var fileContent = $"""
                    host = "{postgresDatabase.Parent.Name}"
                    port = {postgresDatabase.Parent.PrimaryEndpoint.TargetPort}
                    user = "{user}"
                    password = "{password}"
                    database = "{postgresDatabase.DatabaseName}"
                    sslmode = "disable"
                    """;

            bookmarkFiles.Add(new ContainerFile
            {
                Name = $"{postgresDatabase.Name}.toml",
                Contents = fileContent,
            });
        }

        return bookmarkFiles;
    }

    private static async Task<string> WritePgAdminServerJson(IEnumerable<PostgresServerResource> postgresInstances, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteStartObject("Servers");

        var serverIndex = 1;

        foreach (var postgresInstance in postgresInstances)
        {
            var endpoint = postgresInstance.PrimaryEndpoint;
            var userName = postgresInstance.UserNameParameter is null
                ? "postgres"
                : await postgresInstance.UserNameParameter.GetValueAsync(cancellationToken).ConfigureAwait(false);
            var password = await postgresInstance.PasswordParameter.GetValueAsync(cancellationToken).ConfigureAwait(false);

            writer.WriteStartObject($"{serverIndex}");
            writer.WriteString("Name", postgresInstance.Name);
            writer.WriteString("Group", "Servers");
            // PgAdmin assumes Postgres is being accessed over a default Aspire container network and hardcodes the resource address
            // This will need to be refactored once updated service discovery APIs are available
            writer.WriteString("Host", endpoint.Resource.Name);
            writer.WriteNumber("Port", (int)endpoint.TargetPort!);
            writer.WriteString("Username", userName);
            writer.WriteString("SSLMode", "prefer");
            writer.WriteString("MaintenanceDB", "postgres");
            writer.WriteString("PasswordExecCommand", $"echo '{password}'"); // HACK: Generating a pass file and playing around with chmod is too painful.
            writer.WriteEndObject();

            serverIndex++;
        }

        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Gets the appropriate PostgreSQL data directory path based on the image version.
    /// </summary>
    /// <remarks>
    /// PostgreSQL 18+ changed the data directory from /var/lib/postgresql/data to /var/lib/postgresql.
    /// See https://github.com/docker-library/postgres/pull/1259 for more information.
    /// </remarks>
    internal static string GetPostgresDataDirectoryPath(IResourceBuilder<PostgresServerResource> builder)
    {
        if (builder.Resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault() is { } imageAnnotation)
        {
            var tag = imageAnnotation.Tag ?? PostgresContainerImageTags.Tag;
            if (TryParsePostgresMajorVersion(tag, out var majorVersion) && majorVersion >= 18)
            {
                return "/var/lib/postgresql";
            }
        }

        return "/var/lib/postgresql/data";
    }

    /// <summary>
    /// Attempts to parse the PostgreSQL major version from an image tag.
    /// </summary>
    /// <param name="tag">The image tag (e.g., "17.6", "18.1", "18-alpine", "latest").</param>
    /// <param name="majorVersion">The parsed major version number, if successful.</param>
    /// <returns><see langword="true"/> if the major version was successfully parsed; otherwise, <see langword="false"/>.</returns>
    internal static bool TryParsePostgresMajorVersion(string tag, out int majorVersion)
    {
        majorVersion = 0;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        // Handle tags like "18.1-alpine", "17.6-bookworm", etc.
        var versionPart = tag.Split('-')[0];

        // Handle tags like "18.1", "17", etc.
        var parts = versionPart.Split('.');

        return parts.Length > 0 && int.TryParse(parts[0], out majorVersion) && majorVersion > 0;
    }

    private static async Task CreateDatabaseAsync(NpgsqlConnection npgsqlConnection, PostgresDatabaseResource npgsqlDatabase, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var scriptAnnotation = npgsqlDatabase.Annotations.OfType<PostgresCreateDatabaseScriptAnnotation>().LastOrDefault();

        var logger = serviceProvider.GetRequiredService<ResourceLoggerService>().GetLogger(npgsqlDatabase.Parent);
        logger.LogDebug("Creating database '{DatabaseName}'", npgsqlDatabase.DatabaseName);

        try
        {
            var quotedDatabaseIdentifier = new NpgsqlCommandBuilder().QuoteIdentifier(npgsqlDatabase.DatabaseName);
            using var command = npgsqlConnection.CreateCommand();
            command.CommandText = scriptAnnotation?.Script ?? $"CREATE DATABASE {quotedDatabaseIdentifier}";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Database '{DatabaseName}' created successfully", npgsqlDatabase.DatabaseName);
        }
        catch (PostgresException p) when (p.SqlState == "42P04")
        {
            // Ignore the error if the database already exists.
            logger.LogDebug("Database '{DatabaseName}' already exists", npgsqlDatabase.DatabaseName);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to create database '{DatabaseName}'", npgsqlDatabase.DatabaseName);
        }
    }
}
