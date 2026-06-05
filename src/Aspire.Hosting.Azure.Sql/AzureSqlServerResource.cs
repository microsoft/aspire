// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;
using Azure.Provisioning.Sql;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Sql Server resource.
/// </summary>
public class AzureSqlServerResource : AzureProvisioningResource, IResourceWithConnectionString
{
    private readonly Dictionary<string, AzureSqlDatabaseResource> _databases = new Dictionary<string, AzureSqlDatabaseResource>(StringComparers.ResourceName);
    private readonly bool _createdWithInnerResource;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureSqlServerResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
    public AzureSqlServerResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
        : base(name, configureInfrastructure) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureSqlServerResource"/> class.
    /// </summary>
    /// <param name="innerResource">The <see cref="SqlServerServerResource"/> that this resource wraps.</param>
    /// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
    [Obsolete($"This method is obsolete and will be removed in a future version. Use {nameof(AzureSqlExtensions.AddAzureSqlServer)} instead to add an Azure SQL server resource.")]
    public AzureSqlServerResource(SqlServerServerResource innerResource, Action<AzureResourceInfrastructure> configureInfrastructure)
        : base(innerResource.Name, configureInfrastructure)
    {
        InnerResource = innerResource;
        _createdWithInnerResource = true;
    }

    /// <summary>
    /// Gets the fully qualified domain name (FQDN) output reference from the bicep template for the Azure SQL Server resource.
    /// </summary>
    public BicepOutputReference FullyQualifiedDomainName => new("sqlServerFqdn", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    private BicepOutputReference AdminName => new("sqlServerAdminName", this);

    /// <summary>
    /// Gets the host name for the SQL Server.
    /// </summary>
    /// <remarks>
    /// In container mode, resolves to the container's primary endpoint host.
    /// In Azure mode, resolves to the Azure SQL Server's fully qualified domain name.
    /// </remarks>
    public ReferenceExpression HostName =>
        IsContainer ?
            ReferenceExpression.Create($"{InnerResource!.PrimaryEndpoint.Property(EndpointProperty.Host)}") :
            ReferenceExpression.Create($"{FullyQualifiedDomainName}");

    /// <summary>
    /// Gets the port for the PostgreSQL server.
    /// </summary>
    /// <remarks>
    /// In container mode, resolves to the container's primary endpoint port.
    /// In Azure mode, resolves to 1433.
    /// </remarks>
    public ReferenceExpression Port =>
        IsContainer ?
            ReferenceExpression.Create($"{InnerResource.Port}") :
            ReferenceExpression.Create($"1433");

    /// <summary>
    /// Gets the connection URI expression for the SQL Server.
    /// </summary>
    /// <remarks>
    /// Format: <c>mssql://{host}:{port}</c>.
    /// </remarks>
    public ReferenceExpression UriExpression =>
        IsContainer ?
            InnerResource.UriExpression :
            ReferenceExpression.Create($"mssql://{FullyQualifiedDomainName}:1433");

    /// <summary>
    /// Gets the connection template for the manifest for the Azure SQL Server resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            // When the resource was created with an InnerResource (using AsAzure or PublishAsAzure extension methods)
            // the InnerResource will have a ConnectionStringRedirectAnnotation back to this resource. In that case, don't
            // use the InnerResource's ConnectionString, or else it will infinite loop and stack overflow.
            ReferenceExpression? result = null;
            if (!_createdWithInnerResource)
            {
                result = InnerResource?.ConnectionStringExpression;
            }

            return result ??
                ReferenceExpression.Create($"Server=tcp:{FullyQualifiedDomainName},1433;Encrypt=True;Authentication=\"Active Directory Default\"");
        }
    }

    /// <summary>
    /// Gets the inner SqlServerServerResource resource.
    /// 
    /// This is set when RunAsContainer is called on the AzureSqlServerResource resource to create a local SQL Server container.
    /// </summary>
    internal SqlServerServerResource? InnerResource { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the current resource represents a container. If so the actual resource is not running in Azure.
    /// </summary>
    [MemberNotNullWhen(true, nameof(InnerResource))]
    public bool IsContainer => InnerResource is not null;

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => InnerResource?.Annotations ?? base.Annotations;

    /// <summary>
    /// A dictionary where the key is the resource name and the value is the Azure SQL database resource.
    /// </summary>
    public IReadOnlyDictionary<string, AzureSqlDatabaseResource> AzureSqlDatabases => _databases;

    /// <summary>
    /// A dictionary where the key is the resource name and the value is the Azure SQL database name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Databases => _databases.ToDictionary(
        kvp => kvp.Key,
        kvp => kvp.Value.DatabaseName
    );

    internal void AddDatabase(AzureSqlDatabaseResource db)
    {
        _databases.TryAdd(db.Name, db);
    }

    internal void SetInnerResource(SqlServerServerResource innerResource)
    {
        // Copy the annotations to the inner resource before making it the inner resource
        foreach (var annotation in Annotations)
        {
            innerResource.Annotations.Add(annotation);
        }

        InnerResource = innerResource;
    }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a SqlServer with the same identifier already exists
        var existingStore = resources.OfType<SqlServer>().SingleOrDefault(store => store.BicepIdentifier == bicepIdentifier);

        if (existingStore is not null)
        {
            return existingStore;
        }

        // Create and add new resource if it doesn't exist
        var store = SqlServer.FromExisting(bicepIdentifier);

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
        if (this.IsExisting())
        {
            // This resource is already an existing resource, so don't add role assignments
            return;
        }

        var infra = roleAssignmentContext.Infrastructure;
        var sqlserver = (SqlServer)AddAsExistingResource(infra);
        var isRunMode = roleAssignmentContext.ExecutionContext.IsRunMode;

        var sqlServerAdmin = UserAssignedIdentity.FromExisting("sqlServerAdmin");
        sqlServerAdmin.Name = AdminName.AsProvisioningParameter(infra);
        infra.Add(sqlServerAdmin);

        // When not in Run Mode (F5) we reference the managed identity
        // that will need to access the database so we can add db role for it
        // using its ClientId. In the other case we use the PrincipalId.

        var userId = roleAssignmentContext.PrincipalId;

        if (!isRunMode)
        {
            var managedIdentity = UserAssignedIdentity.FromExisting("mi");
            managedIdentity.Name = roleAssignmentContext.PrincipalName;
            infra.Add(managedIdentity);

            userId = managedIdentity.ClientId;
        }

        foreach (var (resource, database) in Databases)
        {
            var uniqueScriptIdentifier = Infrastructure.NormalizeBicepIdentifier($"{this.GetBicepIdentifier()}_{resource}");
            var scriptResource = new AzurePowerShellScript($"script_{uniqueScriptIdentifier}")
            {
                Name = BicepFunction.Take(BicepFunction.Interpolate($"script-{BicepFunction.GetUniqueString(this.GetBicepIdentifier(), roleAssignmentContext.PrincipalName, new StringLiteralExpression(resource), BicepFunction.GetResourceGroup().Id)}"), 24),
                RetentionInterval = TimeSpan.FromHours(1),
                // List of supported versions: https://mcr.microsoft.com/v2/azuredeploymentscripts-powershell/tags/list
                // Using version 14.0 to avoid EOL Ubuntu 20.04 LTS (Bicep linter warning: use-recent-az-powershell-version)
                // Minimum recommended version is 11.0, using 14.0 as the latest supported version.
                AzPowerShellVersion = "14.0"
            };

            // Run the script as the administrator

            var id = BicepFunction.Interpolate($"{sqlServerAdmin.Id}").Compile().ToString();
            scriptResource.Identity.IdentityType = ArmDeploymentScriptManagedIdentityType.UserAssigned;
            scriptResource.Identity.UserAssignedIdentities[id] = new UserAssignedIdentityDetails();

            // Script don't support Bicep expression, they need to be passed as ENVs
            scriptResource.EnvironmentVariables.Add(new ScriptEnvironmentVariable() { Name = "DBNAME", Value = database });
            scriptResource.EnvironmentVariables.Add(new ScriptEnvironmentVariable() { Name = "DBSERVER", Value = sqlserver.FullyQualifiedDomainName });
            scriptResource.EnvironmentVariables.Add(new ScriptEnvironmentVariable() { Name = "PRINCIPALTYPE", Value = roleAssignmentContext.PrincipalType });
            scriptResource.EnvironmentVariables.Add(new ScriptEnvironmentVariable() { Name = "PRINCIPALNAME", Value = roleAssignmentContext.PrincipalName });
            scriptResource.EnvironmentVariables.Add(new ScriptEnvironmentVariable() { Name = "ID", Value = userId });

            scriptResource.ScriptContent = $$"""
                $sqlServerFqdn = "$env:DBSERVER"
                $sqlDatabaseName = "$env:DBNAME"
                $principalName = "$env:PRINCIPALNAME"
                $id = "$env:ID"

                # Install SqlServer module - using specific version to avoid breaking changes in 22.4.5.1 (see https://github.com/dotnet/aspire/issues/9926)
                Install-Module -Name SqlServer -RequiredVersion 22.3.0 -Force -AllowClobber -Scope CurrentUser
                Import-Module SqlServer

                $sqlCmd = @"
                DECLARE @name SYSNAME = '$principalName';
                DECLARE @id UNIQUEIDENTIFIER = '$id';
                
                -- Convert the guid to the right type
                DECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), CONVERT (VARBINARY(16), @id), 1);
                
                -- Construct command: CREATE USER [@name] WITH SID = @castId, TYPE = E;
                DECLARE @cmd NVARCHAR(MAX) = N'CREATE USER [' + @name + '] WITH SID = ' + @castId + ', TYPE = E;'
                EXEC (@cmd);
                
                -- Assign roles to the new user
                DECLARE @role1 NVARCHAR(MAX) = N'ALTER ROLE db_owner ADD MEMBER [' + @name + ']';
                EXEC (@role1);
                
                "@
                # Note: the string terminator must not have whitespace before it, therefore it is not indented.

                Write-Host $sqlCmd

                $connectionString = "Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Authentication=Active Directory Default;"

                Invoke-Sqlcmd -ConnectionString $connectionString -Query $sqlCmd
                """;

            infra.Add(scriptResource);
        }
    }

    internal ReferenceExpression BuildJdbcConnectionString(string? databaseName = null)
    {
        var builder = new ReferenceExpressionBuilder();
        builder.Append($"jdbc:sqlserver://{FullyQualifiedDomainName}:1433;");

        if (!string.IsNullOrEmpty(databaseName))
        {
            var databaseNameReference = ReferenceExpression.Create($"{databaseName:uri}");
            builder.Append($"database={databaseNameReference};");
        }

        builder.AppendLiteral("encrypt=true;trustServerCertificate=false");

        return builder.Build();
    }

    /// <summary>
    /// Gets the JDBC connection string for the server.
    /// </summary>
    /// <remarks>
    /// Format: <c>jdbc:sqlserver://{host}:{port};authentication=ActiveDirectoryIntegrated;encrypt=true;trustServerCertificate=true</c>.
    /// </remarks>
    public ReferenceExpression JdbcConnectionString =>
        IsContainer ?
            InnerResource.JdbcConnectionString :
            BuildJdbcConnectionString();

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        if (IsContainer)
        {
            return ((IResourceWithConnectionString)InnerResource).GetConnectionProperties();
        }

        var result = new Dictionary<string, ReferenceExpression>(
        [
            new ("Host", ReferenceExpression.Create($"{HostName}")),
            new ("Port", ReferenceExpression.Create($"{Port}")),
            new ("Uri", UriExpression),
            new ("JdbcConnectionString", JdbcConnectionString),
        ]);

        return result;
    }
}
