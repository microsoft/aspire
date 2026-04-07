// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.MySql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding MySQL resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class MySqlBuilderExtensions
{
    private const string PasswordEnvVarName = "MYSQL_ROOT_PASSWORD";
    private const UnixFileMode FileMode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>
    /// Adds a MySQL server resource to the application model. For local development a container is used.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="MySqlContainerImageTags.Tag"/> tag of the <inheritdoc cref="MySqlContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="password">The parameter used to provide the root password for the MySQL resource. If <see langword="null"/> a random password will be generated.</param>
    /// <param name="port">The host port for MySQL.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a MySQL server resource")]
    public static IResourceBuilder<MySqlServerResource> AddMySql(this IDistributedApplicationBuilder builder, [ResourceName] string name, IResourceBuilder<ParameterResource>? password = null, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password");

        var resource = new MySqlServerResource(name, passwordParameter);

        string? connectionString = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(resource, async (@event, ct) =>
        {
            connectionString = await resource.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

            if (connectionString == null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{resource.Name}' resource but the connection string was null.");
            }
        });

        builder.Eventing.Subscribe<ResourceReadyEvent>(resource, async (@event, ct) =>
        {
            if (connectionString is null)
            {
                throw new DistributedApplicationException($"ResourceReadyEvent was published for the '{resource.Name}' resource but the connection string was null.");
            }

            using var sqlConnection = new MySqlConnection(connectionString);
            await sqlConnection.OpenAsync(ct).ConfigureAwait(false);

            if (sqlConnection.State != System.Data.ConnectionState.Open)
            {
                throw new InvalidOperationException($"Could not open connection to '{resource.Name}'");
            }

            foreach (var sqlDatabase in resource.DatabaseResources)
            {
                await CreateDatabaseAsync(sqlConnection, sqlDatabase, @event.Services, ct).ConfigureAwait(false);
            }
        });

        var healthCheckKey = $"{name}_check";
        builder.Services.AddHealthChecks().AddMySql(sp => connectionString ?? throw new InvalidOperationException("Connection string is unavailable"), name: healthCheckKey);

        return builder.AddResource(resource)
                      .WithEndpoint(port: port, targetPort: 3306, name: MySqlServerResource.PrimaryEndpointName) // Internal port is always 3306.
                      .WithImage(MySqlContainerImageTags.Image, MySqlContainerImageTags.Tag)
                      .WithImageRegistry(MySqlContainerImageTags.Registry)
                      .WithIconName("DatabaseMultiple")
                      .WithEnvironment(context =>
                      {
                          context.EnvironmentVariables[PasswordEnvVarName] = resource.PasswordParameter;
                      })
                      .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Adds a MySQL database to the application model.
    /// </summary>
    /// <param name="builder">The MySQL server resource builder.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// When adding a <see cref="MySqlDatabaseResource"/> to your application model the resource can then
    /// be referenced by other resources using the resource name. When the dependent resource is using
    /// the extension method <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// then the dependent resource will wait until the MySQL database is available.
    /// </para>
    /// <para>
    /// Note that calling <see cref="AddDatabase(IResourceBuilder{MySqlServerResource}, string, string?)"/>
    /// will result in the database being created on the MySQL server when the server becomes ready.
    /// The database creation happens automatically as part of the resource lifecycle.
    /// </para>
    /// </remarks>
    [AspireExport(Description = "Adds a MySQL database")]
    public static IResourceBuilder<MySqlDatabaseResource> AddDatabase(this IResourceBuilder<MySqlServerResource> builder, [ResourceName] string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        var mySqlDatabase = new MySqlDatabaseResource(name, databaseName, builder.Resource);

        builder.Resource.AddDatabase(mySqlDatabase);

        string? connectionString = null;

        builder.ApplicationBuilder.Eventing.Subscribe<ConnectionStringAvailableEvent>(mySqlDatabase, async (@event, ct) =>
        {
            connectionString = await mySqlDatabase.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

            if (connectionString is null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{name}' resource but the connection string was null.");
            }
        });

        var healthCheckKey = $"{name}_check";
        builder.ApplicationBuilder.Services.AddHealthChecks().AddMySql(sp => connectionString ?? throw new InvalidOperationException("Connection string is unavailable"), name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(mySqlDatabase)
            .WithHealthCheck(healthCheckKey);
    }

    private static async Task CreateDatabaseAsync(MySqlConnection sqlConnection, MySqlDatabaseResource sqlDatabase, IServiceProvider serviceProvider, CancellationToken ct)
    {
        var logger = serviceProvider.GetRequiredService<ResourceLoggerService>().GetLogger(sqlDatabase.Parent);

        logger.LogDebug("Creating database '{DatabaseName}'", sqlDatabase.DatabaseName);

        try
        {
            var scriptAnnotation = sqlDatabase.Annotations.OfType<MySqlCreateDatabaseScriptAnnotation>().LastOrDefault();

            if (scriptAnnotation?.Script is null)
            {
                var quotedDatabaseIdentifier = new MySqlCommandBuilder().QuoteIdentifier(sqlDatabase.DatabaseName);
                using var command = sqlConnection.CreateCommand();
                command.CommandText = $"CREATE DATABASE IF NOT EXISTS {quotedDatabaseIdentifier};";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                using var command = sqlConnection.CreateCommand();
                command.CommandText = scriptAnnotation.Script;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            logger.LogDebug("Database '{DatabaseName}' created successfully", sqlDatabase.DatabaseName);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to create database '{DatabaseName}'", sqlDatabase.DatabaseName);
        }
    }

    /// <summary>
    /// Defines the SQL script used to create the database.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="MySqlDatabaseResource"/>.</param>
    /// <param name="script">The SQL script used to create the database.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <value>Default script is <code>CREATE DATABASE IF NOT EXISTS `QUOTED_DATABASE_NAME`;</code></value>
    /// </remarks>
    [AspireExport(Description = "Defines the SQL script for database creation")]
    public static IResourceBuilder<MySqlDatabaseResource> WithCreationScript(this IResourceBuilder<MySqlDatabaseResource> builder, string script)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(script);

        builder.WithAnnotation(new MySqlCreateDatabaseScriptAnnotation(script));

        return builder;
    }

    /// <summary>
    /// Configures the password that the MySQL resource uses.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="password">The parameter used to provide the password for the MySQL resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Configures the MySQL password")]
    public static IResourceBuilder<MySqlServerResource> WithPassword(this IResourceBuilder<MySqlServerResource> builder, IResourceBuilder<ParameterResource> password)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(password);

        builder.Resource.PasswordParameter = password.Resource;
        return builder;
    }

    /// <summary>
    /// Adds a phpMyAdmin administration and development platform for MySql to the application model.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="MySqlContainerImageTags.PhpMyAdminTag"/> tag of the <inheritdoc cref="MySqlContainerImageTags.PhpMyAdminImage"/> container image.
    /// </remarks>
    /// <param name="builder">The MySql server resource builder.</param>
    /// <param name="configureContainer">Callback to configure PhpMyAdmin container resource.</param>
    /// <param name="containerName">The name of the container (Optional).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds phpMyAdmin management UI", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<T> WithPhpMyAdmin<T>(this IResourceBuilder<T> builder, Action<IResourceBuilder<PhpMyAdminContainerResource>>? configureContainer = null, string? containerName = null) where T : MySqlServerResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.Resources.OfType<PhpMyAdminContainerResource>().SingleOrDefault() is { } existinghpMyAdminResource)
        {
            var builderForExistingResource = builder.ApplicationBuilder.CreateResourceBuilder(existinghpMyAdminResource);
            configureContainer?.Invoke(builderForExistingResource);
            return builder;
        }

        containerName ??= "phpmyadmin";

        var phpMyAdminContainer = new PhpMyAdminContainerResource(containerName);
        var phpMyAdminContainerBuilder = builder.ApplicationBuilder.AddResource(phpMyAdminContainer)
                                                .WithImage(MySqlContainerImageTags.PhpMyAdminImage, MySqlContainerImageTags.PhpMyAdminTag)
                                                .WithImageRegistry(MySqlContainerImageTags.Registry)
                                                .WithHttpEndpoint(targetPort: 80, name: "http")
                                                .WithIconName("WindowDatabase")
                                                .ExcludeFromManifest();

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(phpMyAdminContainer, async (e, ct) =>
        {
            var mySqlInstances = builder.ApplicationBuilder.Resources.OfType<MySqlServerResource>();

            if (!mySqlInstances.Any())
            {
                // No-op if there are no MySql resources present.
                return;
            }

            if (mySqlInstances.Count() == 1)
            {
                var singleInstance = mySqlInstances.Single();
                var endpoint = singleInstance.PrimaryEndpoint;
                phpMyAdminContainerBuilder.WithEnvironment(context =>
                {
                    // PhpMyAdmin assumes MySql is being accessed over a default Aspire container network and hardcodes the resource address
                    // This will need to be refactored once updated service discovery APIs are available
                    context.EnvironmentVariables["PMA_HOST"] = $"{endpoint.Resource.Name}:{endpoint.TargetPort}";
                    context.EnvironmentVariables["PMA_USER"] = "root";
                    context.EnvironmentVariables["PMA_PASSWORD"] = singleInstance.PasswordParameter;
                });
            }
            else
            {
                var fileSystemService = e.Services.GetRequiredService<IFileSystemService>();
                var tempConfigFile = await WritePhpMyAdminConfiguration(fileSystemService, mySqlInstances, ct).ConfigureAwait(false);

                try
                {
                    var aspireStore = e.Services.GetRequiredService<IAspireStore>();

                    // Deterministic file path for the configuration file based on its content
                    var configStoreFilename = aspireStore.GetFileNameWithContent($"{builder.Resource.Name}-config.user.inc.php", tempConfigFile);

                    // Need to grant read access to the config file on unix like systems.
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(configStoreFilename, FileMode644);
                    }

                    phpMyAdminContainerBuilder.WithBindMount(configStoreFilename, "/etc/phpmyadmin/config.user.inc.php");
                }
                finally
                {
                    try
                    {
                        File.Delete(tempConfigFile);
                    }
                    catch
                    {
                    }
                }
            }
        });

        configureContainer?.Invoke(phpMyAdminContainerBuilder);

        return builder;
    }

    /// <summary>
    /// Configures the host port that the PGAdmin resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for PGAdmin.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used, a random port will be assigned.</param>
    /// <returns>The resource builder for PGAdmin.</returns>
    [AspireExport(Description = "Sets the host port for phpMyAdmin")]
    public static IResourceBuilder<PhpMyAdminContainerResource> WithHostPort(this IResourceBuilder<PhpMyAdminContainerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Adds a named volume for the data folder to a MySql container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a data volume for MySQL")]
    public static IResourceBuilder<MySqlServerResource> WithDataVolume(this IResourceBuilder<MySqlServerResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/var/lib/mysql", isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a MySql container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a data bind mount for MySQL")]
    public static IResourceBuilder<MySqlServerResource> WithDataBindMount(this IResourceBuilder<MySqlServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/var/lib/mysql", isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the init folder to a MySql container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete("Use WithInitFiles instead.")]
    public static IResourceBuilder<MySqlServerResource> WithInitBindMount(this IResourceBuilder<MySqlServerResource> builder, string source, bool isReadOnly = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/docker-entrypoint-initdb.d", isReadOnly);
    }

    /// <summary>
    /// Copies init files into a MySql container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source file or directory on the host to copy into the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Copies init files to MySQL")]
    public static IResourceBuilder<MySqlServerResource> WithInitFiles(this IResourceBuilder<MySqlServerResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        const string initPath = "/docker-entrypoint-initdb.d";

        var importFullPath = Path.GetFullPath(source, builder.ApplicationBuilder.AppHostDirectory);

        return builder.WithContainerFiles(initPath, importFullPath);
    }

    private static async Task<string> WritePhpMyAdminConfiguration(IFileSystemService fileSystemService, IEnumerable<MySqlServerResource> mySqlInstances, CancellationToken cancellationToken)
    {
        // This temporary file is not used by the container, it will be copied and then deleted
        var filePath = fileSystemService.TempDirectory.CreateTempFile().Path;

        using var writer = new StreamWriter(filePath);

        writer.WriteLine("<?php");
        writer.WriteLine();
        writer.WriteLine("$i = 0;");
        writer.WriteLine();
        foreach (var mySqlInstance in mySqlInstances)
        {
            var endpoint = mySqlInstance.PrimaryEndpoint;
            var pwd = await mySqlInstance.PasswordParameter.GetValueAsync(cancellationToken).ConfigureAwait(false);

            writer.WriteLine("$i++;");
            // PhpMyAdmin assumes MySql is being accessed over a default Aspire container network and hardcodes the resource address
            // This will need to be refactored once updated service discovery APIs are available
            writer.WriteLine($"$cfg['Servers'][$i]['host'] = '{endpoint.Resource.Name}:{endpoint.TargetPort}';");
            writer.WriteLine($"$cfg['Servers'][$i]['verbose'] = '{mySqlInstance.Name}';");
            writer.WriteLine($"$cfg['Servers'][$i]['auth_type'] = 'cookie';");
            writer.WriteLine($"$cfg['Servers'][$i]['user'] = 'root';");
            writer.WriteLine($"$cfg['Servers'][$i]['password'] = '{pwd}';");
            writer.WriteLine($"$cfg['Servers'][$i]['AllowNoPassword'] = true;");
            writer.WriteLine();
        }
        writer.WriteLine("$cfg['DefaultServer'] = 1;");
        writer.WriteLine("?>");

        return filePath;
    }
}
