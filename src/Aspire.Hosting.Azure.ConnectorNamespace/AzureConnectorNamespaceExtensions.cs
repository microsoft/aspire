// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.ConnectorNamespace.Provisioning;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Microsoft.Extensions.DependencyInjection;

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
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var gatewayResource = (AzureConnectorNamespaceResource)infrastructure.AspireResource;
            var gateway = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(
                infrastructure,
                (identifier, name) =>
                {
                    var existingGateway = ConnectorGateway.FromExisting(identifier);
                    existingGateway.Name = name;
                    return existingGateway;
                },
                infrastructure =>
                {
                    var newGateway = new ConnectorGateway(infrastructure.AspireResource.GetBicepIdentifier())
                    {
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

            var gatewayIdentity = new MemberExpression(new IdentifierExpression(gateway.BicepIdentifier), "identity");
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

                    // Existing resource properties are runtime values and cannot be used for the
                    // early-bound location property. The infrastructure location parameter must
                    // be configured to match the existing Connector Namespace location.
                    if (!gatewayResource.IsExisting())
                    {
                        accessPolicy.Location = gateway.Location;
                    }

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
            }

            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = gateway.Id.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = gateway.Name.ToBicepExpression() });
            if (gatewayResource.IsExisting())
            {
                // Existing Connector Namespaces can use a user-assigned identity, which has no
                // principalId or tenantId fields. Keep the output contract safe and deterministic.
                infrastructure.Add(new ProvisioningOutput("principalId", typeof(string)) { Value = string.Empty });
                infrastructure.Add(new ProvisioningOutput("tenantId", typeof(string)) { Value = string.Empty });
            }
            else
            {
                infrastructure.Add(new ProvisioningOutput("principalId", typeof(string))
                {
                    Value = (BicepValue<string>)new MemberExpression(gatewayIdentity, "principalId")
                });
                infrastructure.Add(new ProvisioningOutput("tenantId", typeof(string))
                {
                    Value = (BicepValue<string>)new MemberExpression(gatewayIdentity, "tenantId")
                });
            }
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
        ValidateUniqueBicepIdentifier(
            builder.Resource,
            ConnectorNamespaceBicepIdentifiers.CreateConnection(builder.Resource.Name, name),
            "Connector connection",
            name);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ObjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TenantId);
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
        ValidateUniqueBicepIdentifier(
            builder.Resource,
            ConnectorNamespaceBicepIdentifiers.CreateMcpServerConfig(builder.Resource.Name, name),
            "MCP server configuration",
            name);
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

        builder.Resource.IsExisting = true;
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
    /// Only the operations listed in <paramref name="options"/> are exposed as MCP tools. Operation
    /// IDs are connector-specific and should be verified against the connector operation metadata.
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

    private static IEnumerable<string> GetConnectorNamespaceBicepIdentifiers(AzureConnectorNamespaceResource connectorNamespace)
    {
        yield return Infrastructure.NormalizeBicepIdentifier(connectorNamespace.Name);

        foreach (var connection in connectorNamespace.Connections)
        {
            yield return connection.BicepIdentifier;

            foreach (var accessPolicy in connection.AccessPolicies)
            {
                yield return accessPolicy.BicepIdentifier;
            }
        }

        foreach (var mcpServerConfig in connectorNamespace.McpServerConfigs)
        {
            yield return mcpServerConfig.BicepIdentifier;
        }
    }

    private static void ValidateUniqueBicepIdentifier(
        AzureConnectorNamespaceResource connectorNamespace,
        string bicepIdentifier,
        string resourceType,
        string resourceName)
    {
        if (GetConnectorNamespaceBicepIdentifiers(connectorNamespace).Contains(
            bicepIdentifier,
            StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{resourceType} resource '{resourceName}' generates a duplicate Bicep identifier on Connector Namespace " +
                $"'{connectorNamespace.Name}'.");
        }
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

        ValidateUniqueBicepIdentifier(
            connection.Parent,
            resourceName,
            "Access policy",
            name);

        return resourceName;
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
