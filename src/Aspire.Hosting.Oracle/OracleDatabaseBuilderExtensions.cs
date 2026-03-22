// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Oracle Database resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class OracleDatabaseBuilderExtensions
{
    private const string PasswordEnvVarName = "ORACLE_PWD";

    /// <summary>
    /// Adds a Oracle Server resource to the application model. A container is used for local development.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="OracleContainerImageTags.Tag"/> tag of the <inheritdoc cref="OracleContainerImageTags.Registry"/>/<inheritdoc cref="OracleContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="password">The parameter used to provide the administrator password for the Oracle Server resource. If <see langword="null"/> a random password will be generated.</param>
    /// <param name="port">The host port for Oracle Server.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addOracle", Description = "Adds an Oracle server resource to the distributed application model.")]
    public static IResourceBuilder<OracleDatabaseServerResource> AddOracle(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource>? password = null,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password");

        var oracleDatabaseServer = new OracleDatabaseServerResource(name, passwordParameter);

        string? connectionString = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(oracleDatabaseServer, async (@event, ct) =>
        {
            connectionString = await oracleDatabaseServer.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

            if (connectionString == null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{oracleDatabaseServer.Name}' resource but the connection string was null.");
            }
        });

        var healthCheckKey = $"{name}_check";
        builder.Services.AddHealthChecks()
            .AddOracle(sp => connectionString ?? throw new InvalidOperationException("Connection string is unavailable"), name: healthCheckKey);

        return builder.AddResource(oracleDatabaseServer)
                      .WithEndpoint(port: port, targetPort: 1521, name: OracleDatabaseServerResource.PrimaryEndpointName)
                      .WithImage(OracleContainerImageTags.Image, OracleContainerImageTags.Tag)
                      .WithImageRegistry(OracleContainerImageTags.Registry)
                      .WithIconName("DatabaseMultiple")
                      .WithEnvironment(context =>
                      {
                          context.EnvironmentVariables[PasswordEnvVarName] = oracleDatabaseServer.PasswordParameter;
                      })
                      .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Adds a Oracle Database database to the application model.
    /// </summary>
    /// <param name="builder">The Oracle Database server resource builder.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addDatabase", Description = "Adds an Oracle database resource to an Oracle server resource.")]
    public static IResourceBuilder<OracleDatabaseResource> AddDatabase(
        this IResourceBuilder<OracleDatabaseServerResource> builder,
        [ResourceName] string name,
        string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        builder.Resource.AddDatabase(name, databaseName);
        var oracleDatabase = new OracleDatabaseResource(name, databaseName, builder.Resource);
        return builder.ApplicationBuilder.AddResource(oracleDatabase);
    }

    /// <summary>
    /// Adds a named volume for the data folder to a Oracle Database server container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withDataVolume", Description = "Adds a persistent data volume to the Oracle server resource.")]
    public static IResourceBuilder<OracleDatabaseServerResource> WithDataVolume(this IResourceBuilder<OracleDatabaseServerResource> builder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/opt/oracle/oradata", false);
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a Oracle Database server container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withDataBindMount", Description = "Mounts a host directory as the Oracle data directory.")]
    public static IResourceBuilder<OracleDatabaseServerResource> WithDataBindMount(this IResourceBuilder<OracleDatabaseServerResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/opt/oracle/oradata", false);
    }

    /// <summary>
    /// Adds a bind mount for the init folder to a Oracle Database server container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete("Use WithInitFiles instead.")]
    public static IResourceBuilder<OracleDatabaseServerResource> WithInitBindMount(this IResourceBuilder<OracleDatabaseServerResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/opt/oracle/scripts/startup", false);
    }

    /// <summary>
    /// Copies init files into a Oracle Database server container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source file or directory on the host to copy into the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withInitFiles", Description = "Copies initialization files into the Oracle container.")]
    public static IResourceBuilder<OracleDatabaseServerResource> WithInitFiles(this IResourceBuilder<OracleDatabaseServerResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        const string initPath = "/docker-entrypoint-initdb.d";

        var importFullPath = Path.GetFullPath(source, builder.ApplicationBuilder.AppHostDirectory);

        return builder.WithContainerFiles(initPath, importFullPath);
    }

    /// <summary>
    /// Adds a bind mount for the database setup folder to a Oracle Database server container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withDbSetupBindMount", Description = "Mounts a host directory as the Oracle DB setup directory.")]
    public static IResourceBuilder<OracleDatabaseServerResource> WithDbSetupBindMount(this IResourceBuilder<OracleDatabaseServerResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/opt/oracle/scripts/setup", false);
    }
}
