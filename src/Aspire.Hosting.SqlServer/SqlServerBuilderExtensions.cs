// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding SQL Server resources to the application model.
/// </summary>
public static partial class SqlServerBuilderExtensions
{
    // GO delimiter format: {spaces?}GO{spaces?}{repeat?}{comment?}
    // https://learn.microsoft.com/sql/t-sql/language-elements/sql-server-utilities-statements-go
    [GeneratedRegex(@"^\s*GO(?<repeat>\s+\d{1,6})?(\s*\-{2,}.*)?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    internal static partial Regex GoStatements();

    /// <summary>
    /// Adds a SQL Server resource to the application model. A container is used for local development.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="SqlServerContainerImageTags.Tag"/> tag of the <inheritdoc cref="SqlServerContainerImageTags.Registry"/>/<inheritdoc cref="SqlServerContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="password">The parameter used to provide the administrator password for the SQL Server resource. If <see langword="null"/> a random password will be generated.</param>
    /// <param name="port">The host port for the SQL Server.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addSqlServer", Description = "Adds a SQL Server container resource")]
    public static IResourceBuilder<SqlServerServerResource> AddSqlServer(this IDistributedApplicationBuilder builder, [ResourceName] string name, IResourceBuilder<ParameterResource>? password = null, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // The password must be at least 8 characters long and contain characters from three of the following four sets: Uppercase letters, Lowercase letters, Base 10 digits, and Symbols
        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", minLower: 1, minUpper: 1, minNumeric: 1);

        var sqlServer = new SqlServerServerResource(name, passwordParameter);

        string? connectionString = null;

        var healthCheckKey = $"{name}_check";
        builder.Services.AddHealthChecks().AddSqlServer(sp => connectionString ?? throw new InvalidOperationException("Connection string is unavailable"), name: healthCheckKey);

        return builder.AddResource(sqlServer)
                      .WithEndpoint(port: port, targetPort: 1433, name: SqlServerServerResource.PrimaryEndpointName)
                      .WithImage(SqlServerContainerImageTags.Image, SqlServerContainerImageTags.Tag)
                      .WithImageRegistry(SqlServerContainerImageTags.Registry)
                      .WithIconName("DatabaseMultiple")
                      .WithEnvironment("ACCEPT_EULA", "Y")
                      .WithEnvironment(context =>
                      {
                          context.EnvironmentVariables["MSSQL_SA_PASSWORD"] = sqlServer.PasswordParameter;
                      })
                      .WithHealthCheck(healthCheckKey)
                      .OnConnectionStringAvailable(async (sqlServer, @event, ct) =>
                      {
                          connectionString = await sqlServer.GetConnectionStringAsync(ct).ConfigureAwait(false);

                          if (connectionString == null)
                          {
                              throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{sqlServer.Name}' resource but the connection string was null.");
                          }
                      })
                      .OnResourceReady(async (sqlServer, @event, ct) =>
                      {
                          if (connectionString is null)
                          {
                              throw new DistributedApplicationException($"ResourceReadyEvent was published for the '{sqlServer.Name}' resource but the connection string was null.");
                          }

                          using var sqlConnection = new SqlConnection(connectionString);
                          await sqlConnection.OpenAsync(ct).ConfigureAwait(false);

                          if (sqlConnection.State != System.Data.ConnectionState.Open)
                          {
                              throw new InvalidOperationException($"Could not open connection to '{sqlServer.Name}'");
                          }

                          foreach (var sqlDatabase in sqlServer.DatabaseResources)
                          {
                              await CreateDatabaseAsync(sqlConnection, sqlDatabase, @event.Services, ct).ConfigureAwait(false);
                          }
                      });
    }

    /// <summary>
    /// Adds a SQL Server database to the application model. This is a child resource of a <see cref="SqlServerServerResource"/>.
    /// </summary>
    /// <param name="builder">The SQL Server resource builders.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// When adding a <see cref="SqlServerDatabaseResource"/> to your application model the resource can then
    /// be referenced by other resources using the resource name. When the dependent resource is using
    /// the extension method <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// then the dependent resource will wait until the SQL Server database is available.
    /// </para>
    /// <para>
    /// Note that calling <see cref="AddDatabase(IResourceBuilder{SqlServerServerResource}, string, string?)"/>
    /// will result in the database being created on the SQL Server when the server becomes ready.
    /// The database creation happens automatically as part of the resource lifecycle.
    /// </para>
    /// </remarks>
    [AspireExport("addDatabase", Description = "Adds a SQL Server database resource")]
    public static IResourceBuilder<SqlServerDatabaseResource> AddDatabase(this IResourceBuilder<SqlServerServerResource> builder, [ResourceName] string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        var sqlServerDatabase = new SqlServerDatabaseResource(name, databaseName, builder.Resource);

        builder.Resource.AddDatabase(sqlServerDatabase);

        string? connectionString = null;

        var healthCheckKey = $"{name}_check";
        builder.ApplicationBuilder.Services.AddHealthChecks().AddSqlServer(sp => connectionString ?? throw new InvalidOperationException("Connection string is unavailable"), name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(sqlServerDatabase)
            .WithHealthCheck(healthCheckKey)
            .OnConnectionStringAvailable(async (sqlServerDatabase, @event, ct) =>
            {
                connectionString = await sqlServerDatabase.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

                if (connectionString == null)
                {
                    throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{name}' resource but the connection string was null.");
                }
            });
    }

    /// <summary>
    /// Adds a named volume for the data folder to a SQL Server resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withDataVolume", Description = "Adds a named volume for the SQL Server data folder")]
    public static IResourceBuilder<SqlServerServerResource> WithDataVolume(this IResourceBuilder<SqlServerServerResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/var/opt/mssql", isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a SQL Server resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The container starts up as non-root and the <paramref name="source"/> directory must be readable by the user that the container runs as.
    /// https://learn.microsoft.com/sql/linux/sql-server-linux-docker-container-configure?view=sql-server-ver16&amp;pivots=cs1-bash#mount-a-host-directory-as-data-volume
    /// </remarks>
    [AspireExport("withDataBindMount", Description = "Adds a bind mount for the SQL Server data folder")]
    public static IResourceBuilder<SqlServerServerResource> WithDataBindMount(this IResourceBuilder<SqlServerServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        if (!OperatingSystem.IsWindows())
        {
            return builder.WithBindMount(source, "/var/opt/mssql", isReadOnly);
        }
        else
        {
            // c.f. https://learn.microsoft.com/sql/linux/sql-server-linux-docker-container-configure?view=sql-server-ver15&pivots=cs1-bash#mount-a-host-directory-as-data-volume

            foreach (var dir in new string[] { "data", "log", "secrets" })
            {
                var path = Path.Combine(source, dir);

                Directory.CreateDirectory(path);

                builder.WithBindMount(path, $"/var/opt/mssql/{dir}", isReadOnly);
            }

            return builder;
        }
    }

    /// <summary>
    /// Defines the SQL script used to create the database.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="SqlServerDatabaseResource"/>.</param>
    /// <param name="script">The SQL script used to create the database.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <value>Default script is <code>IF ( NOT EXISTS ( SELECT 1 FROM sys.databases WHERE name = @DatabaseName ) ) CREATE DATABASE [&lt;QUOTED_DATABASE_NAME%gt;];</code></value>
    /// </remarks>
    [AspireExport("withCreationScript", Description = "Defines the SQL script used to create the database")]
    public static IResourceBuilder<SqlServerDatabaseResource> WithCreationScript(this IResourceBuilder<SqlServerDatabaseResource> builder, string script)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(script);

        builder.WithAnnotation(new SqlServerCreateDatabaseScriptAnnotation(script));

        return builder;
    }

    /// <summary>
    /// Configures the password that the SqlServer resource is used.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="password">The parameter used to provide the password for the SqlServer resource.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withPassword", Description = "Configures the password for the SQL Server resource")]
    public static IResourceBuilder<SqlServerServerResource> WithPassword(this IResourceBuilder<SqlServerServerResource> builder, IResourceBuilder<ParameterResource> password)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(password);

        builder.Resource.SetPassword(password.Resource);
        return builder;
    }

    /// <summary>
    /// Configures the host port that the SqlServer resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withHostPort", Description = "Sets the host port for the SQL Server resource")]
    public static IResourceBuilder<SqlServerServerResource> WithHostPort(this IResourceBuilder<SqlServerServerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEndpoint(SqlServerServerResource.PrimaryEndpointName, endpoint =>
        {
            endpoint.Port = port;
        });
    }

    private static async Task CreateDatabaseAsync(SqlConnection sqlConnection, SqlServerDatabaseResource sqlDatabase, IServiceProvider serviceProvider, CancellationToken ct)
    {
        var logger = serviceProvider.GetRequiredService<ResourceLoggerService>().GetLogger(sqlDatabase.Parent);

        logger.LogDebug("Creating database '{DatabaseName}'", sqlDatabase.DatabaseName);

        try
        {
            var scriptAnnotation = sqlDatabase.Annotations.OfType<SqlServerCreateDatabaseScriptAnnotation>().LastOrDefault();

            if (scriptAnnotation?.Script == null)
            {
                var quotedDatabaseIdentifier = new SqlCommandBuilder().QuoteIdentifier(sqlDatabase.DatabaseName);
                using var command = sqlConnection.CreateCommand();
                command.CommandText = $"IF ( NOT EXISTS ( SELECT 1 FROM sys.databases WHERE name = @DatabaseName ) ) CREATE DATABASE {quotedDatabaseIdentifier};";
                command.Parameters.Add(new SqlParameter("@DatabaseName", sqlDatabase.DatabaseName));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                using var reader = new StringReader(scriptAnnotation.Script);
                var batchBuilder = new StringBuilder();

                while (reader.ReadLine() is { } line)
                {
                    var matchGo = GoStatements().Match(line);

                    if (matchGo.Success)
                    {
                        // Execute the current batch
                        var count = matchGo.Groups["repeat"].Success ? int.Parse(matchGo.Groups["repeat"].Value, CultureInfo.InvariantCulture) : 1;
                        var batch = batchBuilder.ToString();

                        for (var i = 0; i < count; i++)
                        {
                            using var command = sqlConnection.CreateCommand();
                            command.CommandText = batch;
                            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }

                        batchBuilder.Clear();
                    }
                    else
                    {
                        // Prevent batches with only whitespace
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            batchBuilder.AppendLine(line);
                        }
                    }
                }

                // Process the remaining batch lines
                if (batchBuilder.Length > 0)
                {
                    using var command = sqlConnection.CreateCommand();
                    command.CommandText = batchBuilder.ToString();
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            logger.LogDebug("Database '{DatabaseName}' created successfully", sqlDatabase.DatabaseName);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to create database '{DatabaseName}'", sqlDatabase.DatabaseName);
        }
    }
}
