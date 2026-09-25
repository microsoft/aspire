// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable AZPROVISION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Network;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;
using Azure.Provisioning.Sql;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Sql Server resource.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class AzureSqlServerResource : AzureProvisioningResource, IResourceWithConnectionString, IAzurePrivateEndpointTarget, IAzurePrivateEndpointTargetNotification, IAzureNspAssociationTarget
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

    /// <summary>
    /// Gets the "id" output reference for the resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    private BicepOutputReference AdminName => new("sqlServerAdminName", this);

    /// <summary>
    /// Gets or sets the storage account used for deployment scripts.
    /// Set during AddAzureSqlServer and potentially swapped by WithAdminDeploymentScriptStorage
    /// or removed by the preparer if no private endpoint is detected.
    /// </summary>
    internal AzureStorageResource? DeploymentScriptStorage { get; set; }

    internal AzureUserAssignedIdentityResource? AdminIdentity { get; set; }

    internal AzureNetworkSecurityGroupResource? DeploymentScriptNetworkSecurityGroup { get; set; }

    internal List<AzureProvisioningResource> DeploymentScriptDependsOn { get; } = [];

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
    /// <remarks>This property is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore]
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

        // Check for deployment script subnet and storage (for private endpoint scenarios)
        this.TryGetLastAnnotation<AdminDeploymentScriptSubnetAnnotation>(out var subnetAnnotation);

        // Resolve the ACI subnet ID and storage account name for deployment scripts.
        BicepValue<global::Azure.Core.ResourceIdentifier>? aciSubnetId = null;
        BicepValue<string>? deploymentStorageAccountName = null;

        if (subnetAnnotation is not null)
        {
            // Explicit subnet provided by user
            aciSubnetId = subnetAnnotation.Subnet.Id.AsProvisioningParameter(infra);
        }

        if (DeploymentScriptStorage is not null)
        {
            // Storage reference — either auto-created or user-provided
            var existingStorageAccount = (StorageAccount)DeploymentScriptStorage.AddAsExistingResource(infra);

            deploymentStorageAccountName = existingStorageAccount.Name;
        }

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

        // when private endpoints are referencing this SQL server, we need to delay the AzurePowerShellScript
        // until the private endpoints are created, so the script can connect to the SQL server using the private endpoint.
        var dependsOn = new List<ProvisionableResource>();
        foreach (var d in DeploymentScriptDependsOn)
        {
            dependsOn.Add(d.AddAsExistingResource(infra));
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

            // Configure the deployment script to run in a subnet (for private endpoint scenarios)
            if (aciSubnetId is not null)
            {
                scriptResource.ContainerSettings.SubnetIds.Add(
                    new ScriptContainerGroupSubnet()
                    {
                        Id = aciSubnetId
                    });
            }

            // Configure the deployment script to use a storage account (for private endpoint scenarios)
            if (deploymentStorageAccountName is not null)
            {
                scriptResource.StorageAccountSettings.StorageAccountName = deploymentStorageAccountName;
            }

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

            scriptResource.ScriptContent = PrincipalReconciliationScript;

            foreach (var d in dependsOn)
            {
                scriptResource.DependsOn.Add(d);
            }

            infra.Add(scriptResource);
        }
    }

    // The PowerShell that the deployment script runs to provision the managed identity's database
    // principal. Held as a constant so tests can pull the T-SQL out of it and execute it against a
    // real SQL Server, rather than only asserting that the expected text reaches the generated bicep.
    internal const string PrincipalReconciliationScript = """
                $sqlServerFqdn = "$env:DBSERVER"
                $sqlDatabaseName = "$env:DBNAME"
                $principalName = "$env:PRINCIPALNAME"
                $id = "$env:ID"

                # The principal name is interpolated into a T-SQL string literal below. For a user principal
                # it is a UPN, which can legitimately contain an apostrophe (for example o'brien@contoso.com),
                # so double it up to keep the literal well formed.
                $escapedPrincipalName = $principalName.Replace("'", "''")

                $sqlCmd = @"
                DECLARE @name SYSNAME = '$escapedPrincipalName';
                DECLARE @id UNIQUEIDENTIFIER = '$id';
                
                -- The SID of an Entra principal is the raw bytes of its object id. @castId is that same
                -- value rendered as the 0x... literal that CREATE USER ... WITH SID requires.
                DECLARE @sid VARBINARY(16) = CONVERT(VARBINARY(16), @id);
                DECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), @sid, 1);
                
                -- Reconciliation below can drop and recreate the principal, so run the whole sequence as a
                -- single unit. XACT_ABORT rolls the transaction back on any error, so a failure between
                -- DROP USER and CREATE USER cannot leave the database with no user for this identity.
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                
                -- Only external (Entra) users are considered, because that is the only kind this script
                -- creates. sys.database_principals also holds SQL users, Windows users, roles and dbo, and
                -- any of those sharing this name would have a different sid and so look stale - dropping a
                -- principal we do not own, along with its permissions. Ignoring them leaves @existingSid
                -- null, so CREATE USER below fails with 'Msg 15023: User already exists in current
                -- database', which is a visible failure rather than a destructive one.
                DECLARE @existingSid VARBINARY(85) = (SELECT sid FROM sys.database_principals WHERE name = @name AND type = 'E');
                
                -- A user left over from an earlier deployment can carry a stale SID, because deleting and
                -- recreating a managed identity keeps the name but changes the object id. Granting a role to
                -- that principal would report success while the application still failed to log in, so drop
                -- it and let it be recreated against the identity we were actually given.
                IF @existingSid IS NOT NULL AND @existingSid <> @sid
                BEGIN
                    -- QUOTENAME escapes any ']' in the identifier, which a raw '[' + @name + ']' would not.
                    DECLARE @dropCmd NVARCHAR(MAX) = N'DROP USER ' + QUOTENAME(@name);
                    EXEC (@dropCmd);
                    SET @existingSid = NULL;
                END
                
                -- Only create the user when it is missing. This script is re-executed on redeploys, and the
                -- retry loop below can also re-run this batch after a transient failure that occurred *after*
                -- the user was already created. An unguarded CREATE USER would then fail with
                -- 'Msg 15023: User already exists in current database', turning a transient error into a
                -- permanent deployment failure.
                IF @existingSid IS NULL
                BEGIN
                    -- Construct command: CREATE USER [@name] WITH SID = @castId, TYPE = E;
                    DECLARE @cmd NVARCHAR(MAX) = N'CREATE USER ' + QUOTENAME(@name) + N' WITH SID = ' + @castId + N', TYPE = E;'
                    EXEC (@cmd);
                END
                
                -- Assign roles to the user. ALTER ROLE ... ADD MEMBER is a no-op when the principal is already a member.
                DECLARE @role1 NVARCHAR(MAX) = N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@name);
                EXEC (@role1);
                
                COMMIT TRANSACTION;
                
                "@
                # Note: the string terminator must not have whitespace before it, therefore it is not indented.

                Write-Host $sqlCmd

                # This script deliberately avoids the SqlServer PowerShell module (Invoke-Sqlcmd). The Azure
                # deployment script host imports the Az modules before running user scripts, and Az.Resources
                # ships Microsoft.Extensions.Caching.Memory 2.2.0. Importing SqlServer afterwards makes its
                # Always Encrypted Azure Key Vault provider - which is registered unconditionally on the first
                # Invoke-Sqlcmd call, even though nothing here uses Always Encrypted - bind against that older
                # assembly and fail with:
                #   System.MissingMethodException: Method not found: 'Void Microsoft.Extensions.Caching.Memory.MemoryCache..ctor(
                #     Microsoft.Extensions.Options.IOptions`1<Microsoft.Extensions.Caching.Memory.MemoryCacheOptions>)'.
                # Both published SqlServer module versions have hit this class of conflict at some point, and
                # upstream tracks the real fix - proper assembly load context isolation - in
                # https://github.com/microsoft/SQLServerPSModule/issues/31, which is still open. Pinning a module
                # version only works against one combination of Az module and .NET runtime versions in the image:
                # 22.3.0 worked until this image bumped its Az modules, and 22.4.5.1 could not load on the older
                # .NET 6 based images (https://github.com/microsoft/aspire/issues/9926). Rather than track that
                # matrix, use System.Data.SqlClient, which ships in-box with PowerShell in the
                # azuredeploymentscripts-powershell images, together with a managed identity access token.
                # Nothing here needs Always Encrypted. See https://github.com/microsoft/aspire/issues/18892.
                # The token audience is cloud specific - US Gov uses database.usgovcloudapi.net and China uses
                # database.chinacloudapi.cn - so derive it from the deployment script's Az context rather than
                # assuming public cloud. The previous Invoke-Sqlcmd implementation used
                # 'Authentication=Active Directory Default', which let the driver resolve this automatically.
                $sqlDnsSuffix = (Get-AzContext).Environment.SqlDatabaseDnsSuffix
                if ([string]::IsNullOrWhiteSpace($sqlDnsSuffix)) {
                    $sqlDnsSuffix = ".database.windows.net"
                }
                $sqlAudience = "https://" + $sqlDnsSuffix.TrimStart('.') + "/"

                $connectionString = "Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;"

                $maxRetries = 5
                $retryDelay = 60
                $attempt = 0
                $success = $false

                while (-not $success -and $attempt -lt $maxRetries) {
                    $attempt++
                    Write-Host "Attempt $attempt of $maxRetries..."
                    $connection = $null
                    try {
                        # Acquired inside the loop so a transient token failure is retried like any other
                        # failure, rather than aborting the script before the first attempt.
                        $tokenResponse = Get-AzAccessToken -ResourceUrl $sqlAudience

                        # Az.Accounts 5.x returns the token as a SecureString, earlier majors return a plain string.
                        $accessToken = if ($tokenResponse.Token -is [System.Security.SecureString]) {
                            [System.Net.NetworkCredential]::new("", $tokenResponse.Token).Password
                        } else {
                            $tokenResponse.Token
                        }

                        $connection = New-Object System.Data.SqlClient.SqlConnection
                        $connection.ConnectionString = $connectionString
                        $connection.AccessToken = $accessToken
                        $connection.Open()

                        $command = $connection.CreateCommand()
                        $command.CommandText = $sqlCmd
                        [void]$command.ExecuteNonQuery()

                        $success = $true
                        Write-Host "SQL command succeeded on attempt $attempt."
                    } catch {
                        Write-Host "Attempt $attempt failed: $_"
                        if ($attempt -lt $maxRetries) {
                            Write-Host "Retrying in $retryDelay seconds..."
                            Start-Sleep -Seconds $retryDelay
                        } else {
                            throw
                        }
                    } finally {
                        if ($null -ne $connection) {
                            $connection.Dispose()
                        }
                    }
                }
                """;

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

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateLinkGroupIds() => ["sqlServer"];

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateDnsZoneNames() => ["privatelink.database.windows.net"];

    void IAzurePrivateEndpointTargetNotification.OnPrivateEndpointCreated(IResourceBuilder<AzurePrivateEndpointResource> privateEndpoint)
    {
        var builder = privateEndpoint.ApplicationBuilder;

        if (builder.ExecutionContext.IsPublishMode)
        {
            DeploymentScriptDependsOn.Add(privateEndpoint.Resource);

            // Guard: only create deployment script infrastructure once per SQL server.
            // Multiple private endpoints may trigger this, but the admin identity, NSG,
            // storage, and BeforeStartEvent subscription should only be set up once.
            if (AdminIdentity is not null)
            {
                return;
            }

            // Create a deployment script storage account (publish mode only).
            // The BeforeStartEvent handler will remove the default storage if it's no longer
            // needed if the user swapped it via WithAdminDeploymentScriptStorage.
            AzureStorageResource? createdStorage = null;
            if (DeploymentScriptStorage is null)
            {
                DeploymentScriptStorage = CreateDeploymentScriptStorage(builder, builder.CreateResourceBuilder(this)).Resource;
                createdStorage = DeploymentScriptStorage;
            }

            var admin = builder.AddAzureUserAssignedIdentity($"{Name}-admin-identity")
               .WithAnnotation(new ExistingAzureResourceAnnotation(AdminName));
            AdminIdentity = admin.Resource;

            DeploymentScriptNetworkSecurityGroup = builder.AddNetworkSecurityGroup($"{Name}-nsg")
                .WithSecurityRule(new AzureSecurityRule()
                {
                    Name = "allow-outbound-443-AzureActiveDirectory",
                    Priority = 100,
                    Direction = SecurityRuleDirection.Outbound,
                    Access = SecurityRuleAccess.Allow,
                    Protocol = SecurityRuleProtocol.Tcp,
                    SourceAddressPrefix = "*",
                    SourcePortRange = "*",
                    DestinationAddressPrefix = AzureServiceTags.AzureActiveDirectory,
                    DestinationPortRange = "443",
                })
                .WithSecurityRule(new AzureSecurityRule()
                {
                    Name = "allow-outbound-443-Sql",
                    Priority = 200,
                    Direction = SecurityRuleDirection.Outbound,
                    Access = SecurityRuleAccess.Allow,
                    Protocol = SecurityRuleProtocol.Tcp,
                    SourceAddressPrefix = "*",
                    SourcePortRange = "*",
                    DestinationAddressPrefix = AzureServiceTags.Sql,
                    DestinationPortRange = "443",
                }).Resource;

            builder.Eventing.Subscribe<BeforeStartEvent>((data, token) =>
            {
                PrepareDeploymentScriptInfrastructure(data.Model, this, createdStorage);

                return Task.CompletedTask;
            });
        }
    }

    private void RemoveDeploymentScriptStorage(DistributedApplicationModel appModel, AzureStorageResource storage)
    {
        if (ReferenceEquals(DeploymentScriptStorage, storage))
        {
            DeploymentScriptStorage = null;
        }

        appModel.Resources.Remove(storage);
    }

    private sealed class StorageFiles(AzureStorageResource storage) : Resource("files"), IResourceWithParent, IAzurePrivateEndpointTarget
    {
        public BicepOutputReference Id => storage.Id;

        public IResource Parent => storage;

        public IEnumerable<string> GetPrivateDnsZoneNames() => ["privatelink.file.core.windows.net"];

        public IEnumerable<string> GetPrivateLinkGroupIds()
        {
            yield return "file";
        }
    }

    private static IResourceBuilder<AzureStorageResource> CreateDeploymentScriptStorage(IDistributedApplicationBuilder builder, IResourceBuilder<AzureSqlServerResource> azureSqlServer)
    {
        var sqlName = azureSqlServer.Resource.Name;
        var storageName = $"{sqlName.Substring(0, Math.Min(sqlName.Length, 10))}-store";

        return builder.AddAzureStorage(storageName)
            .ConfigureInfrastructure(infra =>
            {
                var sa = infra.GetProvisionableResources().OfType<StorageAccount>().SingleOrDefault()
                    ?? throw new InvalidOperationException("Could not find a StorageAccount resource in the infrastructure.");

                // Deployment scripts require shared key access for file share mounting.
                sa.AllowSharedKeyAccess = true;
            });
    }

    private static void PrepareDeploymentScriptInfrastructure(DistributedApplicationModel appModel, AzureSqlServerResource sql, AzureStorageResource? implicitStorage)
    {
        var hasPe = sql.HasAnnotationOfType<PrivateEndpointTargetAnnotation>();
        var hasRoleAssignments = sql.HasAnnotationOfType<DefaultRoleAssignmentsAnnotation>();

        // When there's no private endpoint or no role assignments (e.g. ClearDefaultRoleAssignments was called),
        // remove all deployment script infrastructure since the deployment scripts won't run.
        if (!hasPe || !hasRoleAssignments)
        {
            if (implicitStorage is not null)
            {
                sql.RemoveDeploymentScriptStorage(appModel, implicitStorage);
            }

            if (sql.AdminIdentity is not null)
            {
                appModel.Resources.Remove(sql.AdminIdentity);
                sql.AdminIdentity = null;
            }

            if (sql.DeploymentScriptNetworkSecurityGroup is not null)
            {
                appModel.Resources.Remove(sql.DeploymentScriptNetworkSecurityGroup);
                sql.DeploymentScriptNetworkSecurityGroup = null;
            }

            return;
        }

        // If the implicitStorage was swapped out by WithAdminDeploymentScriptStorage,
        // remove the original default from the model.
        if (implicitStorage is not null && sql.DeploymentScriptStorage != implicitStorage)
        {
            sql.RemoveDeploymentScriptStorage(appModel, implicitStorage);
        }

        // Find the private endpoint targeting this SQL server to get the VirtualNetwork
        var pe = appModel.Resources.OfType<AzurePrivateEndpointResource>()
            .FirstOrDefault(p => ReferenceEquals(p.Target, sql));

        if (pe is null)
        {
            return;
        }

        var builder = new FakeDistributedApplicationBuilder(appModel);

        // add a role assignment to the DeploymentScriptStorage account so the deploymentScript can mount a file share in it.
        builder.CreateResourceBuilder(sql.AdminIdentity!)
            .WithRoleAssignments(builder.CreateResourceBuilder(sql.DeploymentScriptStorage!), StorageBuiltInRole.StorageFileDataPrivilegedContributor);

        // add a private endpoint to the DeploymentScriptStorage files service so the deploymentScript can access it.
        var peSubnet = builder.CreateResourceBuilder(pe.Subnet);
        var storagePe = peSubnet.AddPrivateEndpoint(builder.CreateResourceBuilder(new StorageFiles(sql.DeploymentScriptStorage!)));
        sql.DeploymentScriptDependsOn.Add(storagePe.Resource);

        AzureSubnetResource aciSubnetResource;
        // Only auto-allocate subnet if user didn't provide one
        if (sql.TryGetLastAnnotation<AdminDeploymentScriptSubnetAnnotation>(out var subnetAnnotation))
        {
            aciSubnetResource = subnetAnnotation.Subnet;

            // User provided an explicit subnet — remove the auto-created NSG since they manage their own
            if (sql.DeploymentScriptNetworkSecurityGroup is { } nsg)
            {
                appModel.Resources.Remove(nsg);
                sql.DeploymentScriptNetworkSecurityGroup = null;
            }
        }
        else
        {
            var vnet = builder.CreateResourceBuilder(peSubnet.Resource.Parent);

            var existingSubnets = appModel.Resources.OfType<AzureSubnetResource>()
                .Where(s => ReferenceEquals(s.Parent, vnet.Resource));

            var aciSubnetCidr = SubnetAddressAllocator.AllocateDeploymentScriptSubnet(vnet.Resource, existingSubnets);
            var aciSubnet = vnet.AddSubnet($"{sql.Name}-aci-subnet", aciSubnetCidr)
                .WithNetworkSecurityGroup(builder.CreateResourceBuilder(sql.DeploymentScriptNetworkSecurityGroup!));
            aciSubnetResource = aciSubnet.Resource;

            sql.Annotations.Add(new AdminDeploymentScriptSubnetAnnotation(aciSubnet.Resource));
        }

        // Always delegate the subnet to ACI. WithServiceDelegation removes existing delegation
        // annotations before appending ACI's required delegation, so an explicit subnet retains a
        // single effective delegation. ACI must replace a caller's previous delegation for the admin
        // deployment script to run in that subnet.
        builder.CreateResourceBuilder(aciSubnetResource)
            .WithServiceDelegation(AzureSubnetServiceDelegations.ContainerInstances);
    }

    private sealed class FakeBuilder<T>(T resource, IDistributedApplicationBuilder applicationBuilder) : IResourceBuilder<T> where T : IResource
    {
        public IDistributedApplicationBuilder ApplicationBuilder => applicationBuilder;
        public T Resource => resource;
        public IResourceBuilder<T> WithAnnotation<TAnnotation>(TAnnotation annotation, ResourceAnnotationMutationBehavior behavior = ResourceAnnotationMutationBehavior.Append) where TAnnotation : IResourceAnnotation
        {
            // Mirror DistributedApplicationResourceBuilder<T>.WithAnnotation's Replace handling so
            // publish-time configuration behaves identically when routed through this builder
            // instead of the real app-model builder.
            if (behavior == ResourceAnnotationMutationBehavior.Replace
                && Resource.Annotations.OfType<TAnnotation>().SingleOrDefault() is { } existingAnnotation)
            {
                Resource.Annotations.Remove(existingAnnotation);
            }

            Resource.Annotations.Add(annotation);
            return this;
        }
    }

    private sealed class FakeDistributedApplicationBuilder(DistributedApplicationModel model) : IDistributedApplicationBuilder
    {
        public IResourceCollection Resources => model.Resources;
        public DistributedApplicationExecutionContext ExecutionContext { get; } = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);

        public IResourceBuilder<T> CreateResourceBuilder<T>(T resource) where T : IResource
        {
            return new FakeBuilder<T>(resource, this);
        }

        public IResourceBuilder<T> AddResource<T>(T resource) where T : IResource
        {
            model.Resources.Add(resource);
            return CreateResourceBuilder(resource);
        }

        public ConfigurationManager Configuration => throw new NotImplementedException();

        public string AppHostDirectory => throw new NotImplementedException();

        public Assembly? AppHostAssembly => throw new NotImplementedException();

        public IHostEnvironment Environment => throw new NotImplementedException();

        public IServiceCollection Services => throw new NotImplementedException();

        public IDistributedApplicationEventing Eventing => throw new NotImplementedException();

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        public IDistributedApplicationPipeline Pipeline => throw new NotImplementedException();
#pragma warning restore ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        public DistributedApplication Build()
        {
            throw new NotImplementedException();
        }
    }
}
