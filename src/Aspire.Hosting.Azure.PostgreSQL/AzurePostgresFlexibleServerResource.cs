// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.PostgreSql;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an resource for Azure Postgres Flexible Server.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Callback to configure infrastructure.</param>
public class AzurePostgresFlexibleServerResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure), IResourceWithEndpoints, IResourceWithConnectionString
{
    private readonly Dictionary<string, string> _databases = new Dictionary<string, string>(StringComparers.ResourceName);

    /// <summary>
    /// Gets the "connectionString" output reference from the bicep template for the Azure Postgres Flexible Server.
    /// 
    /// This is used when Entra ID authentication is used. The connection string is an output of the bicep template.
    /// </summary>
    private BicepOutputReference ConnectionStringOutput => new("connectionString", this);

    /// <summary>
    /// Gets the "connectionString" secret output reference from the bicep template for the Azure Postgres Flexible Server.
    ///
    /// This is set when password authentication is used. The connection string is stored in a secret in the Azure Key Vault.
    /// </summary>
    internal IAzureKeyVaultSecretReference? ConnectionStringSecretOutput { get; set; }

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the "hostName" output reference from the bicep template for the Azure Postgres Flexible Server.
    /// </summary>
    private BicepOutputReference HostNameOutput => new("hostName", this);

    /// <summary>
    /// Gets a value indicating whether the resource uses password authentication.
    /// </summary>
    [MemberNotNullWhen(true, nameof(ConnectionStringSecretOutput))]
    public bool UsePasswordAuthentication => ConnectionStringSecretOutput is not null;

    /// <summary>
    /// Gets the inner PostgresServerResource resource.
    /// 
    /// This is set when RunAsContainer is called on the AzurePostgresFlexibleServerResource resource to create a local PostgreSQL container.
    /// </summary>
    internal PostgresServerResource? InnerResource { get; private set; }

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => InnerResource?.Annotations ?? base.Annotations;

    /// <summary>
    /// Gets or sets the parameter that contains the PostgreSQL server user name.
    /// </summary>
    internal ParameterResource? UserNameParameter { get; set; }

    /// <summary>
    /// Gets or sets the parameter that contains the PostgreSQL server password.
    /// </summary>
    internal ParameterResource? PasswordParameter { get; set; }

    /// <summary>
    /// Gets the host name for the PostgreSQL server.
    /// </summary>
    /// <remarks>
    /// In container mode, resolves to the container's primary endpoint host.
    /// In Azure mode, resolves to the Azure PostgreSQL server's fully qualified domain name.
    /// </remarks>
    public ReferenceExpression HostName =>
        IsContainer ?
            ReferenceExpression.Create($"{InnerResource.PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}") :
            ReferenceExpression.Create($"{HostNameOutput}");

    /// <summary>
    /// Gets the host for the PostgreSQL server.
    /// </summary>
    /// <remarks>
    /// In container mode, resolves to the container's primary endpoint host.
    /// In Azure mode, resolves to the Azure PostgreSQL server's fully qualified domain name.
    /// </remarks>
    public ReferenceExpression Host =>
        IsContainer ?
            ReferenceExpression.Create($"{InnerResource.Host}") :
            ReferenceExpression.Create($"{HostNameOutput}");

    /// <summary>
    /// Gets the port for the PostgreSQL server.
    /// </summary>
    /// <remarks>
    /// In container mode, resolves to the container's primary endpoint port.
    /// In Azure mode, resolves to 5432.
    /// </remarks>
    public ReferenceExpression Port =>
        IsContainer ?
            ReferenceExpression.Create($"{InnerResource.Port}") :
            ReferenceExpression.Create($"5432");

    /// <summary>
    /// Gets the user name for the PostgreSQL server when password authentication is enabled.
    /// </summary>
    /// <remarks>
    /// This property returns null when using Entra ID (Azure Active Directory) authentication.
    /// When password authentication is enabled, it resolves to the user name parameter value.
    /// </remarks>
    public ReferenceExpression? UserName =>
        IsContainer ?
            InnerResource.UserNameReference :
            UsePasswordAuthentication && UserNameParameter is not null ?
                ReferenceExpression.Create($"{UserNameParameter}") :
                null;

    /// <summary>
    /// Gets the password for the PostgreSQL server when password authentication is enabled.
    /// </summary>
    /// <remarks>
    /// This property returns null when using Entra ID (Azure Active Directory) authentication.
    /// When password authentication is enabled, it resolves to the password parameter value.
    /// </remarks>
    public ReferenceExpression? Password =>
        IsContainer && InnerResource.PasswordParameter is not null ?
            ReferenceExpression.Create($"{InnerResource.PasswordParameter}") :
            UsePasswordAuthentication && PasswordParameter is not null ?
                ReferenceExpression.Create($"{PasswordParameter}") :
                null;

    /// <summary>
    /// Gets the connection URI expression for the PostgreSQL server.
    /// </summary>
    /// <remarks>
    /// Format: <c>postgresql://{user}:{password}@{host}:{port}</c>.
    /// </remarks>
    public ReferenceExpression UriExpression =>
        IsContainer ?
        InnerResource.UriExpression :
        UsePasswordAuthentication && PasswordParameter is not null ?
            UserNameParameter is not null ?
                ReferenceExpression.Create($"postgresql://{UserNameParameter:uri}:{PasswordParameter:uri}@{HostNameOutput}") :
                ReferenceExpression.Create($"postgresql://:{PasswordParameter:uri}@{HostNameOutput}") :
            UserNameParameter is not null ?
                ReferenceExpression.Create($"postgresql://{UserNameParameter:uri}@{HostNameOutput}") :
                ReferenceExpression.Create($"postgresql://{HostNameOutput}");

    /// <summary>
    /// Gets a value indicating whether the current resource represents a container. If so the actual resource is not running in Azure.
    /// </summary>
    [MemberNotNullWhen(true, nameof(InnerResource))]
    public bool IsContainer => InnerResource is not null;

    /// <summary>
    /// Gets the connection template for the manifest for the Azure Postgres Flexible Server.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        InnerResource?.ConnectionStringExpression ??
            (UsePasswordAuthentication ?
                ReferenceExpression.Create($"{ConnectionStringSecretOutput}") :
                ReferenceExpression.Create($"{ConnectionStringOutput}"));

    internal ReferenceExpression BuildJdbcConnectionString(string? databaseName = null)
    {
        var builder = new ReferenceExpressionBuilder();
        builder.AppendLiteral("jdbc:postgresql://");
        builder.Append($"{HostNameOutput}");

        if (databaseName is not null)
        {
            builder.Append($"/{databaseName:uri}");
        }

        // Using TLS is mandatory with Azure Database for PostgreSQL flexible server instances
        builder.AppendLiteral("?sslmode=require&authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin");

        return builder.Build();
    }

    /// <summary>
    /// Gets the JDBC connection string for the server.
    /// </summary>
    /// <remarks>
    /// Format: <c>jdbc:postgresql://{host}:{port}?sslmode=require&amp;authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin</c>.
    /// </remarks>
    public ReferenceExpression JdbcConnectionString =>
        IsContainer ?
            InnerResource.JdbcConnectionString :
            BuildJdbcConnectionString();

    /// <summary>
    /// A dictionary where the key is the resource name and the value is the database name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Databases => _databases;

    internal void AddDatabase(string name, string databaseName)
    {
        _databases.TryAdd(name, databaseName);
    }

    internal void SetInnerResource(PostgresServerResource innerResource)
    {
        // Copy the annotations to the inner resource before making it the inner resource
        foreach (var annotation in Annotations)
        {
            innerResource.Annotations.Add(annotation);
        }

        InnerResource = innerResource;
    }

    internal ReferenceExpression GetDatabaseConnectionString(string databaseResourceName, string databaseName)
    {
        // If the server resource is using a secret output, then the database should also use a secret output as well.
        // Note that the bicep template puts each database's connection string in a KeyVault secret.
        if (InnerResource is null && ConnectionStringSecretOutput is not null)
        {
            var dbSecret = ConnectionStringSecretOutput.Resource.GetSecret(GetDatabaseKeyVaultSecretName(databaseResourceName));
            dbSecret.SecretOwner = ConnectionStringSecretOutput.SecretOwner;
            return ReferenceExpression.Create($"{dbSecret}");
        }

        return ReferenceExpression.Create($"{this};Database={databaseName}");
    }

    internal static string GetDatabaseKeyVaultSecretName(string databaseResourceName) => $"connectionstrings--{databaseResourceName}";

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a PostgreSqlFlexibleServer with the same identifier already exists
        var existingStore = resources.OfType<PostgreSqlFlexibleServer>().SingleOrDefault(store => store.BicepIdentifier == bicepIdentifier);

        if (existingStore is not null)
        {
            return existingStore;
        }

        // Create and add new resource if it doesn't exist
        var store = PostgreSqlFlexibleServer.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            store))
        {
            store.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(store);
        return store;
    }

    /// <inheritdoc/>
    public override void AddRoleAssignments(IAddRoleAssignmentsContext roleAssignmentContext)
    {
        Debug.Assert(!UsePasswordAuthentication, "AddRoleAssignments should not be called when using UsePasswordAuthentication");

        var infra = roleAssignmentContext.Infrastructure;
        var postgres = (PostgreSqlFlexibleServer)AddAsExistingResource(infra);

        var principalType = ConvertPrincipalTypeDangerously(roleAssignmentContext.PrincipalType);
        var principalId = roleAssignmentContext.PrincipalId;
        var principalName = roleAssignmentContext.PrincipalName;

        AzurePostgresExtensions.AddActiveDirectoryAdministrator(infra, postgres, principalId, principalType, principalName);
    }

    // Assumes original has a value in PostgreSqlFlexibleServerPrincipalType, will fail at runtime if not
    static BicepValue<PostgreSqlFlexibleServerPrincipalType> ConvertPrincipalTypeDangerously(BicepValue<RoleManagementPrincipalType> original) =>
        original.Compile();

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        var properties = IsContainer
            ? ((IResourceWithConnectionString)InnerResource).GetConnectionProperties()
            : new Dictionary<string, ReferenceExpression>(
                [
                    new("Host", Host),
                    new("Port", Port), // For parity with PostgresServerResource, fixed on Azure
                    new("Uri", UriExpression),
                    new("JdbcConnectionString", JdbcConnectionString),
                ]);

        var propertiesDictionary = properties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparers.ResourceName);

        if (UserNameParameter is not null)
        {
            propertiesDictionary["Username"] = ReferenceExpression.Create($"{UserNameParameter}");
        }

        if (UsePasswordAuthentication && PasswordParameter is not null)
        {
            propertiesDictionary["Password"] = ReferenceExpression.Create($"{PasswordParameter}");
        }

        return propertiesDictionary;
    }
}
