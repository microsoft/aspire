// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Diagnostics.CodeAnalysis;
using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Postgres;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.PostgreSql;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding the Azure PostgreSQL resources to the application model.
/// </summary>
public static class AzurePostgresExtensions
{
    private static IResourceBuilder<T> WithLoginAndPassword<T>(this IResourceBuilder<T> builder, PostgresServerResource postgresResource)
        where T : AzureBicepResource
    {
        var userParam = postgresResource.UserNameParameter ??
            CreateDefaultUserNameParameter(builder);
        builder.WithParameter("administratorLogin", userParam);

        builder.WithParameter("administratorLoginPassword", postgresResource.PasswordParameter);

        return builder;
    }

    [Obsolete]
    private static IResourceBuilder<PostgresServerResource> PublishAsAzurePostgresFlexibleServerInternal(
        this IResourceBuilder<PostgresServerResource> builder,
        bool useProvisioner = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ApplicationBuilder.AddAzureProvisioning();

        var configureInfrastructure = (AzureResourceInfrastructure infrastructure) =>
        {
            var administratorLogin = new ProvisioningParameter("administratorLogin", typeof(string));
            infrastructure.Add(administratorLogin);

            var administratorLoginPassword = new ProvisioningParameter("administratorLoginPassword", typeof(string)) { IsSecure = true };
            infrastructure.Add(administratorLoginPassword);

            var kvNameParam = new ProvisioningParameter("keyVaultName", typeof(string));
            infrastructure.Add(kvNameParam);

            var keyVault = KeyVaultService.FromExisting("keyVault");
            keyVault.Name = kvNameParam;
            infrastructure.Add(keyVault);

            var postgres = CreatePostgreSqlFlexibleServer(infrastructure, builder.ApplicationBuilder, builder.Resource.Databases);
            postgres.AdministratorLogin = administratorLogin;
            postgres.AdministratorLoginPassword = administratorLoginPassword;

            var secret = new KeyVaultSecret("connectionString")
            {
                Parent = keyVault,
                Name = "connectionString",
                Properties = new SecretProperties
                {
                    Value = BicepFunction.Interpolate($"Host={postgres.FullyQualifiedDomainName};Username={administratorLogin};Password={administratorLoginPassword}")
                }
            };
            infrastructure.Add(secret);
        };

        var resource = new AzurePostgresResource(builder.Resource, configureInfrastructure);
        var resourceBuilder = builder.ApplicationBuilder.CreateResourceBuilder(resource)
                                                        .WithManifestPublishingCallback(resource.WriteToManifest)
                                                        .WithLoginAndPassword(builder.Resource);

        if (useProvisioner)
        {
            // Used to hold a reference to the azure surrogate for use with the provisioner.
            builder.WithAnnotation(new AzureBicepResourceAnnotation(resource));
            builder.WithConnectionStringRedirection(resource);

            // Remove the container annotation so that DCP doesn't do anything with it.
            if (builder.Resource.Annotations.OfType<ContainerImageAnnotation>().SingleOrDefault() is { } containerAnnotation)
            {
                builder.Resource.Annotations.Remove(containerAnnotation);
            }
        }

        return builder;
    }

    /// <summary>
    /// Configures Postgres Server resource to be deployed as Azure PostgreSQL Flexible Server.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{PostgresServerResource}"/> builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{PostgresServerResource}"/> builder.</returns>
    [Obsolete($"This method is obsolete and will be removed in a future version. Use {nameof(AddAzurePostgresFlexibleServer)} instead to add an Azure PostgreSQL Flexible Server resource.")]
    public static IResourceBuilder<PostgresServerResource> PublishAsAzurePostgresFlexibleServer(this IResourceBuilder<PostgresServerResource> builder)
        => PublishAsAzurePostgresFlexibleServerInternal(builder, useProvisioner: false);

    /// <summary>
    /// Configures resource to use Azure for local development and when doing a deployment via the Azure Developer CLI.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{PostgresServerResource}"/> builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{PostgresServerResource}"/> builder.</returns>
    [Obsolete($"This method is obsolete and will be removed in a future version. Use {nameof(AddAzurePostgresFlexibleServer)} instead to add an Azure PostgreSQL Flexible Server resource.")]
    public static IResourceBuilder<PostgresServerResource> AsAzurePostgresFlexibleServer(this IResourceBuilder<PostgresServerResource> builder)
        => PublishAsAzurePostgresFlexibleServerInternal(builder, useProvisioner: true);

    /// <summary>
    /// Adds an Azure PostgreSQL Flexible Server resource to the application model.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzurePostgresFlexibleServerResource}"/> builder.</returns>
    /// <remarks>
    /// By default, the Azure PostgreSQL Flexible Server resource is configured to use Microsoft Entra ID (Azure Active Directory) for authentication.
    /// This requires changes to the application code to use an azure credential to authenticate with the resource. See
    /// https://learn.microsoft.com/azure/postgresql/flexible-server/how-to-connect-with-managed-identity#connect-using-managed-identity-in-c for more information.
    ///
    /// You can use the <see cref="WithPasswordAuthentication(IResourceBuilder{AzurePostgresFlexibleServerResource}, IResourceBuilder{IAzureKeyVaultResource}, IResourceBuilder{ParameterResource}?, IResourceBuilder{ParameterResource}?)"/> method to configure the resource to use password authentication.
    /// <example>
    /// The following example creates an Azure PostgreSQL Flexible Server resource and referencing that resource in a .NET project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var data = builder.AddAzurePostgresFlexibleServer("data");
    ///
    /// builder.AddProject&lt;Projects.ProductService&gt;()
    ///     .WithReference(data);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport("addAzurePostgresFlexibleServer", Description = "Adds an Azure PostgreSQL Flexible Server resource")]
    public static IResourceBuilder<AzurePostgresFlexibleServerResource> AddAzurePostgresFlexibleServer(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var resource = new AzurePostgresFlexibleServerResource(name, infrastructure => ConfigurePostgreSqlInfrastructure(infrastructure, builder));
        return builder.AddResource(resource)
            .WithIconName("DatabaseMultiple")
            .WithAnnotation(new DefaultRoleAssignmentsAnnotation(new HashSet<RoleDefinition>()));
    }

    /// <summary>
    /// Adds an Azure PostgreSQL database to the application model.
    /// </summary>
    /// <param name="builder">The Azure PostgreSQL server resource builder.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addDatabase", Description = "Adds an Azure PostgreSQL database")]
    public static IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> AddDatabase(this IResourceBuilder<AzurePostgresFlexibleServerResource> builder, [ResourceName] string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        var azureResource = builder.Resource;
        var azurePostgresDatabase = new AzurePostgresFlexibleServerDatabaseResource(name, databaseName, azureResource);

        builder.Resource.AddDatabase(name, databaseName);

        if (azureResource.InnerResource is null)
        {
            return builder.ApplicationBuilder.AddResource(azurePostgresDatabase);
        }
        else
        {
            // need to add the database to the InnerResource
            var innerBuilder = builder.ApplicationBuilder.CreateResourceBuilder(azureResource.InnerResource);
            var innerDb = innerBuilder.AddDatabase(name, databaseName);
            azurePostgresDatabase.SetInnerResource(innerDb.Resource);

            // create a builder, but don't add the Azure database to the model because the InnerResource already has it
            return builder.ApplicationBuilder.CreateResourceBuilder(azurePostgresDatabase);
        }
    }

    /// <summary>
    /// Configures an Azure PostgreSQL Flexible Server resource to run locally in a container.
    /// </summary>
    /// <param name="builder">The Azure PostgreSQL server resource builder.</param>
    /// <param name="configureContainer">Callback that exposes underlying container to allow for customization.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzurePostgresFlexibleServerResource}"/> builder.</returns>
    /// <remarks>
    /// <example>
    /// The following example creates an Azure PostgreSQL Flexible Server resource that runs locally in a
    /// PostgreSQL container and referencing that resource in a .NET project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var data = builder.AddAzurePostgresFlexibleServer("data")
    ///     .RunAsContainer();
    ///
    /// builder.AddProject&lt;Projects.ProductService&gt;()
    ///     .WithReference(data);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport("runAsContainer", Description = "Configures the Azure PostgreSQL Flexible Server resource to run locally in a container", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<AzurePostgresFlexibleServerResource> RunAsContainer(this IResourceBuilder<AzurePostgresFlexibleServerResource> builder, Action<IResourceBuilder<PostgresServerResource>>? configureContainer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var azureResource = builder.Resource;
        var azureDatabases = builder.ApplicationBuilder.Resources
            .OfType<AzurePostgresFlexibleServerDatabaseResource>()
            .Where(db => db.Parent == azureResource)
            .ToDictionary(db => db.Name);

        RemoveAzureResources(builder.ApplicationBuilder, azureResource, azureDatabases);

        var userNameParameterBuilder = azureResource.UserNameParameter is not null ?
            builder.ApplicationBuilder.CreateResourceBuilder(azureResource.UserNameParameter) :
            null;
        var passwordParameterBuilder = azureResource.PasswordParameter is not null ?
            builder.ApplicationBuilder.CreateResourceBuilder(azureResource.PasswordParameter) :
            null;

        var postgresContainer = builder.ApplicationBuilder.AddPostgres(
            azureResource.Name,
            userNameParameterBuilder,
            passwordParameterBuilder);

        azureResource.SetInnerResource(postgresContainer.Resource);

        foreach (var database in azureResource.Databases)
        {
            if (!azureDatabases.TryGetValue(database.Key, out var existingDb))
            {
                throw new InvalidOperationException($"Could not find a {nameof(AzurePostgresFlexibleServerDatabaseResource)} with name {database.Key}.");
            }

            var innerDb = postgresContainer.AddDatabase(database.Key, database.Value);
            existingDb.SetInnerResource(innerDb.Resource);
        }

        configureContainer?.Invoke(postgresContainer);

        return builder;
    }

    private static void RemoveAzureResources(IDistributedApplicationBuilder appBuilder, AzurePostgresFlexibleServerResource azureResource, Dictionary<string, AzurePostgresFlexibleServerDatabaseResource> azureDatabases)
    {
        appBuilder.Resources.Remove(azureResource);
        foreach (var database in azureDatabases)
        {
            appBuilder.Resources.Remove(database.Value);
        }
    }

    /// <summary>
    /// Configures the resource to use password authentication for Azure PostgreSQL Flexible Server.
    /// </summary>
    /// <param name="builder">The Azure PostgreSQL server resource builder.</param>
    /// <param name="userName">The parameter used to provide the user name for the PostgreSQL resource. If <see langword="null"/> a default value will be used.</param>
    /// <param name="password">The parameter used to provide the administrator password for the PostgreSQL resource. If <see langword="null"/> a random password will be generated.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzurePostgresFlexibleServerResource}"/> builder.</returns>
    /// <remarks>
    /// <example>
    /// The following example creates an Azure PostgreSQL Flexible Server resource that uses password authentication.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var data = builder.AddAzurePostgresFlexibleServer("data")
    ///     .WithPasswordAuthentication();
    ///
    /// builder.AddProject&lt;Projects.ProductService&gt;()
    ///     .WithReference(data);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport("withPasswordAuthentication", Description = "Configures password authentication for Azure PostgreSQL Flexible Server")]
    public static IResourceBuilder<AzurePostgresFlexibleServerResource> WithPasswordAuthentication(
        this IResourceBuilder<AzurePostgresFlexibleServerResource> builder,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var kv = builder.ApplicationBuilder.AddAzureKeyVault($"{builder.Resource.Name}-kv")
                                           .WithParentRelationship(builder.Resource);

        // remove the KeyVault from the model if the emulator is used during run mode.
        // need to do this later in case builder becomes an emulator after this method is called.
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            builder.ApplicationBuilder.OnBeforeStart((data, token) =>
            {
                if (builder.Resource.IsContainer())
                {
                    data.Model.Resources.Remove(kv.Resource);
                }
                return Task.CompletedTask;
            });
        }

        return builder.WithPasswordAuthentication(kv, userName, password);
    }

    /// <summary>
    /// Configures the resource to use password authentication for Azure PostgreSQL Flexible Server.
    /// This overload is used when the PostgreSQL resource is created in a container and the password is stored in an Azure Key Vault secret.
    /// </summary>
    /// <param name="builder">The Azure PostgreSQL server resource builder.</param>
    /// <param name="keyVaultBuilder">The Azure Key Vault resource builder.</param>
    /// <param name="userName">The parameter used to provide the user name for the PostgreSQL resource. If <see langword="null"/> a default value will be used.</param>
    /// <param name="password">The parameter used to provide the administrator password for the PostgreSQL resource. If <see langword="null"/> a random password will be generated.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> builder.</returns>
    [AspireExport("withPasswordAuthenticationWithKeyVault", Description = "Configures password authentication using a specified Azure Key Vault resource")]
    public static IResourceBuilder<AzurePostgresFlexibleServerResource> WithPasswordAuthentication(
        this IResourceBuilder<AzurePostgresFlexibleServerResource> builder,
        IResourceBuilder<IAzureKeyVaultResource> keyVaultBuilder,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var azureResource = builder.Resource;

        azureResource.UserNameParameter = userName?.Resource ??
            CreateDefaultUserNameParameter(builder);
        builder.WithParameter("administratorLogin", azureResource.UserNameParameter);

        azureResource.PasswordParameter = password?.Resource ??
            ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder.ApplicationBuilder, $"{builder.Resource.Name}-password");
        builder.WithParameter("administratorLoginPassword", azureResource.PasswordParameter);

        azureResource.ConnectionStringSecretOutput = keyVaultBuilder.Resource.GetSecret($"connectionstrings--{builder.Resource.Name}");
        // Set the secret owner to this resource
        azureResource.ConnectionStringSecretOutput.SecretOwner = azureResource;

        // If someone already called RunAsContainer - we need to reset the username/password parameters on the InnerResource
        var containerResource = azureResource.InnerResource;
        if (containerResource is not null)
        {
            containerResource.UserNameParameter = azureResource.UserNameParameter;
            containerResource.PasswordParameter = azureResource.PasswordParameter;
        }

        // remove role assignment annotations when using password authentication so an empty roles bicep module isn't generated
        var roleAssignmentAnnotations = azureResource.Annotations.OfType<DefaultRoleAssignmentsAnnotation>().ToArray();
        foreach (var annotation in roleAssignmentAnnotations)
        {
            azureResource.Annotations.Remove(annotation);
        }

        return builder;
    }

    /// <summary>
    /// Adds a Postgres MCP server container and configures it to connect to the database represented by <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The Azure Postgres database resource builder.</param>
    /// <param name="configureContainer">Configuration callback for the Postgres MCP container resource.</param>
    /// <param name="containerName">The name of the container (optional).</param>
    /// <remarks>
    /// <para>
    /// The Postgres MCP server is configured to use SSE transport and will expose an HTTP endpoint.
    /// </para>
    /// <para>
    /// This extension method only applies when the Azure PostgreSQL resource is running as a container (i.e., <see cref="AzurePostgresFlexibleServerDatabaseResource.IsContainer"/> is <see langword="true"/>).
    /// If the resource is not running as a container, this method has no effect.
    /// </para>
    /// </remarks>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withPostgresMcp", Description = "Adds a Postgres MCP server container", RunSyncOnBackgroundThread = true)]
    [Experimental("ASPIREPOSTGRES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> WithPostgresMcp(
        this IResourceBuilder<AzurePostgresFlexibleServerDatabaseResource> builder,
        Action<IResourceBuilder<PostgresMcpContainerResource>>? configureContainer = null,
        string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Only apply when running as a container
        if (!builder.Resource.IsContainer)
        {
            return builder;
        }

        // Delegate to the underlying PostgresDatabaseResource's WithPostgresMcp
        var innerBuilder = builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.InnerResource);
        innerBuilder.WithPostgresMcp(configureContainer, containerName);

        return builder;
    }

    private static PostgreSqlFlexibleServer CreatePostgreSqlFlexibleServer(AzureResourceInfrastructure infrastructure, IDistributedApplicationBuilder distributedApplicationBuilder, IReadOnlyDictionary<string, string> databases)
    {
        // Check if this PostgreSQL server has a private endpoint (via annotation)
        var hasPrivateEndpoint = infrastructure.AspireResource.HasAnnotationOfType<PrivateEndpointTargetAnnotation>();

        var postgres = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
            (identifier, name) =>
            {
                var resource = PostgreSqlFlexibleServer.FromExisting(identifier);
                resource.Name = name;
                return resource;
            },
            (infrastructure) =>
            {
                var server = new PostgreSqlFlexibleServer(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    StorageSizeInGB = 32,
                    Sku = new PostgreSqlFlexibleServerSku()
                    {
                        Name = "Standard_B1ms",
                        Tier = PostgreSqlFlexibleServerSkuTier.Burstable
                    },
                    Version = new StringLiteralExpression("16"),
                    HighAvailability = new PostgreSqlFlexibleServerHighAvailability()
                    {
                        Mode = PostgreSqlFlexibleServerHighAvailabilityMode.Disabled
                    },
                    Backup = new PostgreSqlFlexibleServerBackupProperties()
                    {
                        BackupRetentionDays = 7,
                        GeoRedundantBackup = PostgreSqlFlexibleServerGeoRedundantBackupEnum.Disabled
                    },
                    AvailabilityZone = "1",
                    Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                };

                // When using private endpoints, disable public network access.
                if (hasPrivateEndpoint)
                {
                    server.Network = new PostgreSqlFlexibleServerNetwork()
                    {
                        PublicNetworkAccess = PostgreSqlFlexibleServerPublicNetworkAccessState.Disabled
                    };
                }

                return server;
            });

        // Only add firewall rules when not using private endpoints
        if (!hasPrivateEndpoint)
        {
            // Opens access to all Azure services.
            infrastructure.Add(new PostgreSqlFlexibleServerFirewallRule("postgreSqlFirewallRule_AllowAllAzureIps")
            {
                Parent = postgres,
                Name = "AllowAllAzureIps",
                StartIPAddress = new IPAddress([0, 0, 0, 0]),
                EndIPAddress = new IPAddress([0, 0, 0, 0])
            });

            if (distributedApplicationBuilder.ExecutionContext.IsRunMode)
            {
                // Opens access to the Internet.
                infrastructure.Add(new PostgreSqlFlexibleServerFirewallRule("postgreSqlFirewallRule_AllowAllIps")
                {
                    Parent = postgres,
                    Name = "AllowAllIps",
                    StartIPAddress = new IPAddress([0, 0, 0, 0]),
                    EndIPAddress = new IPAddress([255, 255, 255, 255])
                });
            }
        }

        foreach (var databaseNames in databases)
        {
            var bicepIdentifier = Infrastructure.NormalizeBicepIdentifier(databaseNames.Key);
            var databaseName = databaseNames.Value;
            var pgsqlDatabase = new PostgreSqlFlexibleServerDatabase(bicepIdentifier)
            {
                Parent = postgres,
                Name = databaseName
            };
            infrastructure.Add(pgsqlDatabase);
        }

        return postgres;
    }

    private static void ConfigurePostgreSqlInfrastructure(AzureResourceInfrastructure infrastructure, IDistributedApplicationBuilder distributedApplicationBuilder)
    {
        var azureResource = (AzurePostgresFlexibleServerResource)infrastructure.AspireResource;
        var postgres = CreatePostgreSqlFlexibleServer(infrastructure, distributedApplicationBuilder, azureResource.Databases);

        if (azureResource.UsePasswordAuthentication)
        {
            var administratorLogin = new ProvisioningParameter("administratorLogin", typeof(string));
            infrastructure.Add(administratorLogin);

            var administratorLoginPassword = new ProvisioningParameter("administratorLoginPassword", typeof(string)) { IsSecure = true };
            infrastructure.Add(administratorLoginPassword);

            var kvNameParam = azureResource.ConnectionStringSecretOutput.Resource.NameOutputReference.AsProvisioningParameter(infrastructure);

            var keyVault = KeyVaultService.FromExisting("keyVault");
            keyVault.Name = kvNameParam;
            infrastructure.Add(keyVault);

            // bicep doesn't allow for setting properties on existing resources. So we don't set auth properties here.
            // The administratorLogin and administratorLoginPassword are expected to match what is already configured on the server
            if (!postgres.IsExistingResource)
            {
                postgres.AuthConfig = new PostgreSqlFlexibleServerAuthConfig()
                {
                    ActiveDirectoryAuth = PostgreSqlFlexibleServerActiveDirectoryAuthEnum.Disabled,
                    PasswordAuth = PostgreSqlFlexibleServerPasswordAuthEnum.Enabled
                };

                postgres.AdministratorLogin = administratorLogin;
                postgres.AdministratorLoginPassword = administratorLoginPassword;
            }

            var secret = new KeyVaultSecret("connectionString")
            {
                Parent = keyVault,
                Name = $"connectionstrings--{azureResource.Name}",
                Properties = new SecretProperties
                {
                    Value = BicepFunction.Interpolate($"Host={postgres.FullyQualifiedDomainName};Username={administratorLogin};Password={administratorLoginPassword}")
                }
            };
            infrastructure.Add(secret);

            foreach (var database in azureResource.Databases)
            {
                var dbSecret = new KeyVaultSecret(Infrastructure.NormalizeBicepIdentifier(database.Key + "_connectionString"))
                {
                    Parent = keyVault,
                    Name = AzurePostgresFlexibleServerResource.GetDatabaseKeyVaultSecretName(database.Key),
                    Properties = new SecretProperties
                    {
                        Value = BicepFunction.Interpolate($"Host={postgres.FullyQualifiedDomainName};Username={administratorLogin};Password={administratorLoginPassword};Database={database.Value}")
                    }
                };
                infrastructure.Add(dbSecret);
            }
        }
        else
        {
            if (!postgres.IsExistingResource)
            {
                postgres.AuthConfig = new PostgreSqlFlexibleServerAuthConfig()
                {
                    ActiveDirectoryAuth = PostgreSqlFlexibleServerActiveDirectoryAuthEnum.Enabled,
                    PasswordAuth = PostgreSqlFlexibleServerPasswordAuthEnum.Disabled
                };
            }

            // We don't know the principalName, so we can't add it to the connection string.
            // The user name will need to come from the application code.
            infrastructure.Add(new ProvisioningOutput("connectionString", typeof(string))
            {
                Value = BicepFunction.Interpolate($"Host={postgres.FullyQualifiedDomainName}")
            });
        }

        // We need to output name to externalize role assignments.
        infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = postgres.Name.ToBicepExpression() });

        // Output the resource id for private endpoint support.
        infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = postgres.Id.ToBicepExpression() });

        // Always output the hostName for the PostgreSQL server.
        infrastructure.Add(new ProvisioningOutput("hostName", typeof(string)) { Value = postgres.FullyQualifiedDomainName.ToBicepExpression() });
    }

    internal static PostgreSqlFlexibleServerActiveDirectoryAdministrator AddActiveDirectoryAdministrator(AzureResourceInfrastructure infra, PostgreSqlFlexibleServer postgres, BicepValue<Guid> principalId, BicepValue<PostgreSqlFlexibleServerPrincipalType> principalType, BicepValue<string> principalName)
    {
        var admin = new PostgreSqlFlexibleServerActiveDirectoryAdministrator($"{postgres.BicepIdentifier}_admin")
        {
            Parent = postgres,
            Name = principalId,
            PrincipalType = principalType,
            PrincipalName = principalName,
        };
        infra.Add(admin);
        return admin;
    }

    private static ParameterResource CreateDefaultUserNameParameter<T>(IResourceBuilder<T> builder) where T : AzureBicepResource
    {
        var generatedUserName = new GenerateParameterDefault
        {
            MinLength = 10,
            // just use letters for the username since it can't start with a number
            Numeric = false,
            Special = false
        };

        return ParameterResourceBuilderExtensions.CreateGeneratedParameter(
            builder.ApplicationBuilder, $"{builder.Resource.Name}-username", secret: false, generatedUserName);
    }
}
