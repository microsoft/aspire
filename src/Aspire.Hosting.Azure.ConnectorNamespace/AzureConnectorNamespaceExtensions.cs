// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.ConnectorNamespace.Provisioning;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Azure Connector Namespace resources to the application model.
/// </summary>
public static class AzureConnectorNamespaceExtensions
{
    /// <summary>
    /// Adds an Azure Connector Namespace resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A resource builder for the Connector Namespace.</returns>
    /// <remarks>
    /// Connector Namespace is a preview service. Connections that use OAuth or another interactive
    /// authorization flow must be authorized in the Connector Namespaces portal after provisioning.
    /// </remarks>
    /// <example>
    /// This example provisions a Connector Namespace with an Office 365 connection and exposes an
    /// allow-listed operation through a managed MCP server:
    /// <code>
    /// var connectors = builder.AddAzureConnectorNamespace("connectors");
    /// var office365 = connectors.AddConnection("office365", "office365");
    ///
    /// connectors.AddMcpServerConfig("mcp")
    ///     .WithConnector("mail", office365, new AzureConnectorNamespaceMcpConnectorOptions
    ///     {
    ///         Operations =
    ///         [
    ///             new AzureConnectorNamespaceMcpOperationOptions { Name = "SendEmailV2" }
    ///         ]
    ///     })
    ///     .WithAccessPolicy("developer-access", new AzureConnectorNamespaceMcpAccessPolicyOptions
    ///     {
    ///         ObjectId = "11111111-1111-1111-1111-111111111111",
    ///         TenantId = "22222222-2222-2222-2222-222222222222",
    ///         PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
    ///     });
    /// </code>
    /// </example>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureConnectorNamespaceResource> AddAzureConnectorNamespace(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.AddAzureProvisioning();

        static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var gatewayResource = (AzureConnectorNamespaceResource)infrastructure.AspireResource;
            var gatewayBicepIdentifier = ConnectorNamespaceBicepIdentifiers.Gateway;
            var gateway = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(
                infrastructure,
                (_, name) =>
                {
                    var existingGateway = ConnectorGateway.FromExisting(gatewayBicepIdentifier);
                    existingGateway.Name = name;
                    return existingGateway;
                },
                _ =>
                {
                    var gatewayNamePrefix = ConnectorNamespaceBicepIdentifiers.CreateGatewayResourceNamePrefix(
                        gatewayResource.Name);
                    var newGateway = new ConnectorGateway(gatewayBicepIdentifier)
                    {
                        Name = BicepFunction.Interpolate(
                            $"{gatewayNamePrefix}{BicepFunction.GetUniqueString(
                                BicepFunction.GetResourceGroup().Id,
                                new StringLiteralExpression(gatewayResource.Name))}"),
                        Properties = [],
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };
                    newGateway.Identity.ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssigned;
                    return newGateway;
                });

            var connectionMap = new Dictionary<AzureConnectorNamespaceConnectionResource, ConnectorGatewayConnection>();
            foreach (var connectionResource in gatewayResource.Connections)
            {
                var connection = connectionResource.IsExisting
                    ? ConnectorGatewayConnection.FromExisting(connectionResource.BicepIdentifier)
                    : new ConnectorGatewayConnection(connectionResource.BicepIdentifier);
                connection.Parent = gateway;
                connection.Name = connectionResource.ConnectionName;

                if (!connectionResource.IsExisting)
                {
                    connection.DisplayName = connectionResource.DisplayName ?? connectionResource.ConnectionName;
                    connection.ConnectorName = connectionResource.ConnectorName;
                }

                infrastructure.Add(connection);
                connectionMap.Add(connectionResource, connection);
            }

            foreach (var connectionResource in gatewayResource.Connections)
            {
                var connection = connectionMap[connectionResource];
                foreach (var accessPolicyResource in connectionResource.AccessPolicies)
                {
                    var accessPolicy = new ConnectorGatewayConnectionAccessPolicy(
                        accessPolicyResource.BicepIdentifier)
                    {
                        Parent = connection,
                        Name = accessPolicyResource.PolicyName
                    };

                    // Preview Connector Namespace types do not have Bicep type metadata, so parent
                    // location references are runtime values that cannot populate this property.
                    // Leaving it unset uses the infrastructure's early-bound location parameter.

                    accessPolicy.Principal.Type = "ActiveDirectory";
                    if (accessPolicyResource.IdentityResource is { } identityResource)
                    {
                        accessPolicy.Principal.Identity.ObjectId = identityResource.PrincipalId.AsProvisioningParameter(infrastructure);
                        accessPolicy.Principal.Identity.TenantId = BicepFunction.GetTenant().TenantId;
                    }
                    else
                    {
                        accessPolicy.Principal.Identity.ObjectId = accessPolicyResource.ObjectId;
                        accessPolicy.Principal.Identity.TenantId = accessPolicyResource.TenantId;
                    }

                    infrastructure.Add(accessPolicy);
                }
            }

            foreach (var configResource in gatewayResource.McpServerConfigs)
            {
                if (!configResource.IsExisting && configResource.Connectors.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"MCP server configuration '{configResource.Name}' requires a connector. " +
                        $"Call '{nameof(WithConnector)}' before generating the Azure deployment.");
                }

                if (!configResource.IsExisting && configResource.AccessPolicies.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"MCP server configuration '{configResource.Name}' requires an access policy. " +
                        $"Call '{nameof(WithAccessPolicy)}' before generating the Azure deployment.");
                }

                var config = configResource.IsExisting
                    ? ConnectorGatewayMcpServerConfig.FromExisting(configResource.BicepIdentifier)
                    : new ConnectorGatewayMcpServerConfig(configResource.BicepIdentifier);
                config.Parent = gateway;
                config.Name = configResource.ConfigName;

                if (!configResource.IsExisting)
                {
                    config.Kind = "ManagedMcpServer";
                    config.State = "Enabled";

                    if (!string.IsNullOrWhiteSpace(configResource.Description))
                    {
                        config.Description = configResource.Description;
                    }

                    foreach (var connectorDefinition in configResource.Connectors)
                    {
                        var connection = connectionMap[connectorDefinition.Connection];
                        config.DependsOn.Add(connection);

                        var connector = new ConnectorGatewayMcpConnector
                        {
                            Name = connectorDefinition.Name,
                            ConnectionName = connectorDefinition.Connection.ConnectionName,
                            DisplayName = connectorDefinition.DisplayName ?? connectorDefinition.Name
                        };

                        if (!string.IsNullOrWhiteSpace(connectorDefinition.Description))
                        {
                            connector.Description = connectorDefinition.Description;
                        }

                        foreach (var operationDefinition in connectorDefinition.Operations)
                        {
                            var operation = new ConnectorGatewayMcpOperation
                            {
                                Name = operationDefinition.Name,
                                DisplayName = operationDefinition.DisplayName ?? operationDefinition.Name
                            };

                            if (!string.IsNullOrWhiteSpace(operationDefinition.Description))
                            {
                                operation.Description = operationDefinition.Description;
                            }

                            connector.Operations.Add(operation);
                        }

                        config.Connectors.Add(connector);
                    }
                }

                infrastructure.Add(config);

                foreach (var accessPolicyResource in configResource.AccessPolicies)
                {
                    var accessPolicy = new ConnectorGatewayMcpServerConfigAccessPolicy(
                        accessPolicyResource.BicepIdentifier)
                    {
                        Parent = config,
                        // The service requires the access-policy resource name to match the principal object ID.
                        Name = accessPolicyResource.ObjectId,
                        PrincipalType = accessPolicyResource.PrincipalType.ToString()
                    };

                    // Preview Connector Namespace types do not have Bicep type metadata, so parent
                    // location references are runtime values that cannot populate this property.
                    // Leaving it unset uses the infrastructure's early-bound location parameter.

                    accessPolicy.Principal.Type = "ActiveDirectory";
                    accessPolicy.Principal.Identity.ObjectId = accessPolicyResource.ObjectId;
                    accessPolicy.Principal.Identity.TenantId = accessPolicyResource.TenantId;
                    infrastructure.Add(accessPolicy);
                }
            }

            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = gateway.Id.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = gateway.Name.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("principalId", typeof(string))
            {
                Value = gatewayResource.IsExisting()
                    ? GetOptionalIdentityProperty(gateway, "principalId")
                    : gateway.Identity.PrincipalId
            });
            infrastructure.Add(new ProvisioningOutput("tenantId", typeof(string))
            {
                Value = gatewayResource.IsExisting()
                    ? GetOptionalIdentityProperty(gateway, "tenantId")
                    : gateway.Identity.TenantId
            });
        }

        return builder.AddResource(new AzureConnectorNamespaceResource(name, ConfigureInfrastructure));
    }

    /// <summary>
    /// Adds a connection to an Azure Connector Namespace.
    /// </summary>
    /// <param name="builder">The Connector Namespace resource builder.</param>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="connectorName">The connector catalog name, such as <c>office365</c>.</param>
    /// <param name="options">The optional connection configuration.</param>
    /// <returns>A resource builder for the connection.</returns>
    /// <remarks>
    /// This method provisions the connection resource but does not automate OAuth consent or other
    /// interactive authorization. Complete those steps in the Connector Namespaces portal.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureConnectorNamespaceConnectionResource> AddConnection(
        this IResourceBuilder<AzureConnectorNamespaceResource> builder,
        [ResourceName] string name,
        string connectorName,
        AzureConnectorNamespaceConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        var connectionName = options?.ConnectionName ?? name;
        ValidateConnectorResourceName(connectionName, nameof(options));
        if (builder.Resource.Connections.Any(connection =>
            string.Equals(connection.ConnectionName, connectionName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Connector connection '{connectionName}' is already registered on Connector Namespace '{builder.Resource.Name}'.");
        }

        var connection = new AzureConnectorNamespaceConnectionResource(
            name,
            connectionName,
            connectorName,
            options?.DisplayName,
            builder.Resource);
        connection.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
        builder.Resource.Connections.Add(connection);
        return builder.ApplicationBuilder.AddResource(connection);
    }

    /// <summary>
    /// Adds a Connector Namespace connection reference to a destination resource.
    /// </summary>
    /// <param name="builder">The destination resource builder.</param>
    /// <param name="connection">The Connector Namespace connection resource.</param>
    /// <param name="connectionName">
    /// The optional configuration prefix. The connection's Aspire resource name is used when omitted.
    /// </param>
    /// <returns>The destination resource builder.</returns>
    /// <remarks>
    /// The reference injects <c>{connectionName}__connectorGatewayName</c> and
    /// <c>{connectionName}__connectionName</c> for the Azure Connector SDK.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the generic withReference export.")]
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<AzureConnectorNamespaceConnectionResource> connection,
        string? connectionName = null)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connection);
        if (connectionName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        }

        var configurationName = connectionName ?? connection.Resource.Name;
        builder.WithEnvironment(
            $"{configurationName}__connectorGatewayName",
            connection.Resource.Parent.NameOutputReference);
        builder.WithEnvironment(
            $"{configurationName}__connectionName",
            connection.Resource.ConnectionName);

        return builder;
    }

    /// <summary>
    /// Marks a Connector Namespace connection as an existing Azure resource.
    /// </summary>
    /// <param name="builder">The connection resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("asExistingConnectorNamespaceConnection", MethodName = "asExisting")]
    public static IResourceBuilder<AzureConnectorNamespaceConnectionResource> AsExisting(
        this IResourceBuilder<AzureConnectorNamespaceConnectionResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!string.IsNullOrWhiteSpace(builder.Resource.DisplayName))
        {
            throw new InvalidOperationException(
                $"Connector connection '{builder.Resource.Name}' configures a display name and cannot be marked as existing.");
        }
        if (builder.Resource.AccessPolicies.Count > 0)
        {
            throw new InvalidOperationException(
                $"Connector connection '{builder.Resource.Name}' configures access policies and cannot be marked as existing.");
        }

        builder.Resource.IsExisting = true;
        return builder;
    }

    /// <summary>
    /// Adds a Microsoft Entra access policy to a Connector Namespace connection.
    /// </summary>
    /// <param name="builder">The connection resource builder.</param>
    /// <param name="name">The Aspire resource name for the policy.</param>
    /// <param name="options">The authorized principal and optional Azure policy name.</param>
    /// <returns>The connection resource builder.</returns>
    /// <remarks>
    /// Access policies authorize a specific Microsoft Entra principal to use the connection. They do
    /// not perform connector OAuth consent and should be limited to principals that require access.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureConnectorNamespaceConnectionResource> WithAccessPolicy(
        this IResourceBuilder<AzureConnectorNamespaceConnectionResource> builder,
        [ResourceName] string name,
        AzureConnectorNamespaceAccessPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ValidateEntraPrincipalIds(
            options.ObjectId,
            options.TenantId,
            "connection access policy",
            nameof(options.ObjectId),
            nameof(options.TenantId),
            nameof(options));
        if (builder.Resource.IsExisting)
        {
            throw new InvalidOperationException(
                $"Existing connector connection '{builder.Resource.Name}' is read-only and cannot create an access policy.");
        }

        var policyName = options.PolicyName ?? name;
        ValidateConnectorResourceName(policyName, nameof(options));
        var resourceName = GetValidatedAccessPolicyResourceName(builder.Resource, name, policyName);

        builder.Resource.AccessPolicies.Add(new AzureConnectorNamespaceConnectionAccessPolicyResource(
            resourceName,
            policyName,
            builder.Resource,
            options.ObjectId,
            options.TenantId));
        return builder;
    }

    /// <summary>
    /// Adds a connection access policy for a user-assigned managed identity.
    /// </summary>
    /// <param name="builder">The connection resource builder.</param>
    /// <param name="name">The Aspire resource name for the policy.</param>
    /// <param name="identity">The user-assigned managed identity authorized to use the connection.</param>
    /// <param name="policyName">The optional Azure child resource name. The Aspire resource name is used when omitted.</param>
    /// <returns>The connection resource builder.</returns>
    /// <remarks>
    /// This method authorizes the identity to use the connection. The connection's downstream OAuth,
    /// API key, or basic authentication must still be configured separately.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureConnectorNamespaceConnectionResource> WithIdentityAccessPolicy(
        this IResourceBuilder<AzureConnectorNamespaceConnectionResource> builder,
        [ResourceName] string name,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity,
        string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(identity);
        if (builder.Resource.IsExisting)
        {
            throw new InvalidOperationException(
                $"Existing connector connection '{builder.Resource.Name}' is read-only and cannot create an access policy.");
        }

        policyName ??= name;
        ValidateConnectorResourceName(policyName, nameof(policyName));
        var resourceName = GetValidatedAccessPolicyResourceName(builder.Resource, name, policyName);

        builder.Resource.AccessPolicies.Add(
            AzureConnectorNamespaceConnectionAccessPolicyResource.CreateUserAssignedIdentityPolicy(
                resourceName,
                policyName,
                builder.Resource,
                identity.Resource));
        return builder;
    }

    /// <summary>
    /// Adds a managed MCP server configuration to an Azure Connector Namespace.
    /// </summary>
    /// <param name="builder">The Connector Namespace resource builder.</param>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="options">The optional MCP server configuration.</param>
    /// <returns>A resource builder for the MCP server configuration.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureConnectorNamespaceMcpServerConfigResource> AddMcpServerConfig(
        this IResourceBuilder<AzureConnectorNamespaceResource> builder,
        [ResourceName] string name,
        AzureConnectorNamespaceMcpServerConfigOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var configName = options?.ConfigName ?? name;
        ValidateConnectorResourceName(configName, nameof(options));
        if (builder.Resource.McpServerConfigs.Any(config =>
            string.Equals(config.ConfigName, configName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"MCP server configuration '{configName}' is already registered on Connector Namespace '{builder.Resource.Name}'.");
        }

        var config = new AzureConnectorNamespaceMcpServerConfigResource(
            name,
            configName,
            options?.Description,
            builder.Resource);
        config.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
        builder.Resource.McpServerConfigs.Add(config);
        return builder.ApplicationBuilder.AddResource(config);
    }

    /// <summary>
    /// Marks a managed MCP server configuration as an existing Azure resource.
    /// </summary>
    /// <param name="builder">The MCP server configuration resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("asExistingConnectorNamespaceMcpServerConfig", MethodName = "asExisting")]
    public static IResourceBuilder<AzureConnectorNamespaceMcpServerConfigResource> AsExisting(
        this IResourceBuilder<AzureConnectorNamespaceMcpServerConfigResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Resource.Connectors.Count > 0)
        {
            throw new InvalidOperationException(
                $"MCP server configuration '{builder.Resource.Name}' has connector routes and cannot be marked as existing.");
        }

        if (!string.IsNullOrWhiteSpace(builder.Resource.Description))
        {
            throw new InvalidOperationException(
                $"MCP server configuration '{builder.Resource.Name}' configures a description and cannot be marked as existing.");
        }

        if (builder.Resource.AccessPolicies.Count > 0)
        {
            throw new InvalidOperationException(
                $"MCP server configuration '{builder.Resource.Name}' configures access policies and cannot be marked as existing.");
        }

        builder.Resource.IsExisting = true;
        return builder;
    }

    /// <summary>
    /// Adds a Microsoft Entra user or group access policy to a managed MCP server configuration.
    /// </summary>
    /// <param name="builder">The MCP server configuration resource builder.</param>
    /// <param name="name">The Aspire resource name for the policy.</param>
    /// <param name="options">The authorized user or group.</param>
    /// <returns>The MCP server configuration resource builder.</returns>
    /// <remarks>
    /// Managed MCP endpoints reject callers that do not have a config-scoped access policy.
    /// Connector Namespace currently supports Microsoft Entra users and groups for these policies.
    /// The Azure child resource name is set to the principal object ID as required by the service.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withMcpServerConfigAccessPolicy", MethodName = "withAccessPolicy")]
    public static IResourceBuilder<AzureConnectorNamespaceMcpServerConfigResource> WithAccessPolicy(
        this IResourceBuilder<AzureConnectorNamespaceMcpServerConfigResource> builder,
        [ResourceName] string name,
        AzureConnectorNamespaceMcpAccessPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ValidateMcpAccessPolicyOptions(options);
        if (builder.Resource.IsExisting)
        {
            throw new InvalidOperationException(
                $"Existing MCP server configuration '{builder.Resource.Name}' is read-only and cannot create an access policy.");
        }

        var resourceName = GetValidatedMcpAccessPolicyResourceName(builder.Resource, name, options.ObjectId);
        builder.Resource.AccessPolicies.Add(new AzureConnectorNamespaceMcpAccessPolicyResource(
            resourceName,
            builder.Resource,
            options.ObjectId,
            options.TenantId,
            options.PrincipalType));
        return builder;
    }

    /// <summary>
    /// Adds a connector route and an explicit operation allow-list to a managed MCP server configuration.
    /// </summary>
    /// <param name="builder">The MCP server configuration resource builder.</param>
    /// <param name="connectorName">The connector route name.</param>
    /// <param name="connection">The connection used by the connector route.</param>
    /// <param name="options">The connector metadata and operation allow-list.</param>
    /// <returns>The MCP server configuration resource builder.</returns>
    /// <remarks>
    /// Connections and MCP server configurations are separate Azure resources so a connection can be
    /// reused independently. This method links the MCP server configuration to its underlying connection
    /// and exposes only the operations listed in <paramref name="options"/> as MCP tools. Operation IDs
    /// are connector-specific and should be verified against the connector operation metadata. The current
    /// Connector Namespace preview supports one connector per managed MCP server configuration.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureConnectorNamespaceMcpServerConfigResource> WithConnector(
        this IResourceBuilder<AzureConnectorNamespaceMcpServerConfigResource> builder,
        string connectorName,
        IResourceBuilder<AzureConnectorNamespaceConnectionResource> connection,
        AzureConnectorNamespaceMcpConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        if (builder.Resource.IsExisting)
        {
            throw new InvalidOperationException(
                $"Existing MCP server configuration '{builder.Resource.Name}' is read-only.");
        }

        if (!ReferenceEquals(builder.Resource.Parent, connection.Resource.Parent))
        {
            throw new InvalidOperationException(
                $"Connector connection '{connection.Resource.Name}' belongs to a different Connector Namespace.");
        }

        if (builder.Resource.Connectors.Count > 0)
        {
            throw new InvalidOperationException(
                $"MCP server configuration '{builder.Resource.Name}' already has a connector. " +
                "The current Connector Namespace preview supports one connector per MCP server configuration.");
        }

        if (options.Operations is null || options.Operations.Length == 0)
        {
            throw new ArgumentException(
                "At least one connector operation must be explicitly allow-listed.",
                nameof(options));
        }

        var operationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectorDefinition = new AzureConnectorNamespaceMcpConnectorDefinition(
            connectorName,
            options.DisplayName,
            options.Description,
            connection.Resource);
        foreach (var operation in options.Operations)
        {
            if (operation is null)
            {
                throw new ArgumentException("Connector operations cannot contain null values.", nameof(options));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
            if (!operationNames.Add(operation.Name))
            {
                throw new ArgumentException(
                    $"Connector operation '{operation.Name}' is configured more than once.",
                    nameof(options));
            }

            connectorDefinition.Operations.Add(new AzureConnectorNamespaceMcpOperationDefinition(
                operation.Name,
                operation.DisplayName,
                operation.Description));
        }

        builder.Resource.Connectors.Add(connectorDefinition);
        return builder;
    }

    private static BicepValue<string> GetOptionalIdentityProperty(ConnectorGateway gateway, string propertyName)
    {
        // Existing namespaces can have no identity or a user-assigned-only identity. Safe member access
        // preserves available system-assigned values without making output evaluation fail when absent.
        var identity = new SafeMemberExpression(
            new IdentifierExpression(gateway.BicepIdentifier),
            "identity");
        var value = new SafeMemberExpression(identity, propertyName);
        return new BinaryExpression(
            value,
            BinaryBicepOperator.Coalesce,
            new StringLiteralExpression(string.Empty));
    }

    private static string GetValidatedAccessPolicyResourceName(
        AzureConnectorNamespaceConnectionResource connection,
        string name,
        string policyName)
    {
        var resourceName = ConnectorNamespaceBicepIdentifiers.CreateAccessPolicy(
            connection.Parent.Name,
            connection.Name,
            name);
        if (connection.AccessPolicies.Any(policy =>
            string.Equals(policy.BicepIdentifier, resourceName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Access policy resource '{name}' is already registered on connector connection '{connection.Name}'.");
        }

        if (connection.AccessPolicies.Any(policy =>
            string.Equals(policy.PolicyName, policyName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Access policy '{policyName}' is already registered on connector connection '{connection.Name}'.");
        }

        return resourceName;
    }

    private static string GetValidatedMcpAccessPolicyResourceName(
        AzureConnectorNamespaceMcpServerConfigResource config,
        string name,
        string objectId)
    {
        var resourceName = ConnectorNamespaceBicepIdentifiers.CreateMcpAccessPolicy(
            config.Parent.Name,
            config.Name,
            name);
        if (config.AccessPolicies.Any(policy =>
            string.Equals(policy.BicepIdentifier, resourceName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Access policy resource '{name}' is already registered on MCP server configuration '{config.Name}'.");
        }

        if (config.AccessPolicies.Any(policy =>
            string.Equals(policy.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"An access policy for principal '{objectId}' is already registered on MCP server configuration '{config.Name}'.");
        }

        return resourceName;
    }

    private static void ValidateMcpAccessPolicyOptions(AzureConnectorNamespaceMcpAccessPolicyOptions options)
    {
        ValidateEntraPrincipalIds(
            options.ObjectId,
            options.TenantId,
            "MCP access policy",
            nameof(options.ObjectId),
            nameof(options.TenantId),
            nameof(options));

        if (!Enum.IsDefined(options.PrincipalType))
        {
            throw new ArgumentException(
                $"'{options.PrincipalType}' is not a supported MCP access policy principal type.",
                nameof(options));
        }
    }

    private static void ValidateEntraPrincipalIds(
        string objectId,
        string tenantId,
        string policyDescription,
        string objectIdParamName,
        string tenantIdParamName,
        string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId, objectIdParamName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId, tenantIdParamName);
        if (!Guid.TryParse(objectId, out _))
        {
            throw new ArgumentException($"The {policyDescription} object ID must be a valid GUID.", paramName);
        }

        if (!Guid.TryParse(tenantId, out _))
        {
            throw new ArgumentException($"The {policyDescription} tenant ID must be a valid GUID.", paramName);
        }
    }

    private static void ValidateConnectorResourceName(string name, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);
        if (name.Length is < 2 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                name,
                "Connector Namespace resource names must contain between 2 and 64 characters.");
        }

        if (name.Any(static character =>
            !char.IsAsciiLetterOrDigit(character) &&
            character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Connector Namespace resource names can contain only ASCII letters, numbers, hyphens, and underscores.",
                paramName);
        }
    }

}
