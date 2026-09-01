// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIREAZURE001

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Sandboxes.Provisioning;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Azure Container Apps sandbox resources to the application model.
/// </summary>
public static class AzureSandboxesExtensions
{
    // https://learn.microsoft.com/azure/role-based-access-control/built-in-roles#container-apps-sandboxgroup-data-owner
    private const string SandboxGroupDataOwnerRoleId = "c24cf47c-5077-412d-a19c-45202126392c";

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
                        Location = BicepFunction.GetResourceGroup().Location,
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };
                    newGateway.Identity.ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssigned;
                    return newGateway;
                });

            var connectionMap = new Dictionary<AzureConnectorNamespaceConnectionResource, ConnectorGatewayConnection>();
            foreach (var connectionResource in gatewayResource.Connections)
            {
                var connection = connectionResource.IsExisting
                    ? ConnectorGatewayConnection.FromExisting(Infrastructure.NormalizeBicepIdentifier(connectionResource.Name))
                    : new ConnectorGatewayConnection(Infrastructure.NormalizeBicepIdentifier(connectionResource.Name));
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
                        Infrastructure.NormalizeBicepIdentifier(accessPolicyResource.Name))
                    {
                        Parent = connection,
                        Name = accessPolicyResource.PolicyName,
                        Location = gateway.Location
                    };

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
                var config = configResource.IsExisting
                    ? ConnectorGatewayMcpServerConfig.FromExisting(Infrastructure.NormalizeBicepIdentifier(configResource.Name))
                    : new ConnectorGatewayMcpServerConfig(Infrastructure.NormalizeBicepIdentifier(configResource.Name));
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
                            ConnectionName = connectorDefinition.Connection.ConnectionName
                        };

                        if (!string.IsNullOrWhiteSpace(connectorDefinition.DisplayName))
                        {
                            connector.DisplayName = connectorDefinition.DisplayName;
                        }

                        if (!string.IsNullOrWhiteSpace(connectorDefinition.Description))
                        {
                            connector.Description = connectorDefinition.Description;
                        }

                        foreach (var operationDefinition in connectorDefinition.Operations)
                        {
                            var operation = new ConnectorGatewayMcpOperation
                            {
                                Name = operationDefinition.Name
                            };

                            if (!string.IsNullOrWhiteSpace(operationDefinition.DisplayName))
                            {
                                operation.DisplayName = operationDefinition.DisplayName;
                            }

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
            GetConnectorNamespaceChildren(builder.Resource),
            name,
            "Connector connection",
            builder.Resource.Name);
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
            GetConnectorNamespaceChildren(builder.Resource),
            name,
            "MCP server configuration",
            builder.Resource.Name);
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

    /// <summary>
    /// Adds an Azure Container Apps sandbox group resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A resource builder for the sandbox group.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> AddAzureSandboxGroup(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var sandboxResource = (AzureSandboxGroupResource)infrastructure.AspireResource;
            UserAssignedIdentity? newImagePullIdentity = null;
            BicepValue<string> imagePullIdentityId;
            BicepValue<string> imagePullIdentityClientId;
            if (sandboxResource.TryGetLastAnnotation<AzureSandboxGroupAcrPullIdentityAnnotation>(out var imagePullIdentityAnnotation))
            {
                if (sandboxResource.IsExisting() &&
                    (imagePullIdentityAnnotation.IsAspireManaged || !imagePullIdentityAnnotation.Identity.IsExisting()))
                {
                    throw CreateExistingSandboxGroupMissingAcrPullIdentityException(sandboxResource);
                }

                imagePullIdentityId = imagePullIdentityAnnotation.Identity.Id.AsProvisioningParameter(infrastructure);
                imagePullIdentityClientId = imagePullIdentityAnnotation.Identity.ClientId.AsProvisioningParameter(infrastructure);
            }
            else
            {
                if (sandboxResource.IsExisting())
                {
                    throw CreateExistingSandboxGroupMissingAcrPullIdentityException(sandboxResource);
                }

                newImagePullIdentity = new UserAssignedIdentity(
                    Infrastructure.NormalizeBicepIdentifier($"{sandboxResource.Name}_mi"));
                infrastructure.Add(newImagePullIdentity);
                imagePullIdentityId = newImagePullIdentity.Id.ToBicepExpression();
                imagePullIdentityClientId = newImagePullIdentity.ClientId.ToBicepExpression();
            }

            var sandboxGroup = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = SandboxGroup.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                infrastructure =>
                {
                    var resource = new SandboxGroup(infrastructure.AspireResource.GetBicepIdentifier())
                    {
                        Properties = [],
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };
                    ApplyManagedServiceIdentity(resource.Identity, sandboxResource, imagePullIdentityId, infrastructure);
                    return resource;
                });

            if (newImagePullIdentity is not null)
            {
                var registry = sandboxResource.ContainerRegistry ??
                    throw new InvalidOperationException($"No container registry associated with Azure sandbox group '{sandboxResource.Name}'. This should have been added automatically.");
                var containerRegistry = (ContainerRegistryService)registry.AddAsExistingResource(infrastructure);
                infrastructure.Add(containerRegistry);
                var pullRoleAssignment = containerRegistry.CreateRoleAssignment(
                    ContainerRegistryBuiltInRole.AcrPull,
                    newImagePullIdentity);
                // Azure.Provisioning does not currently generate a stable role-assignment name.
                // See https://github.com/Azure/azure-sdk-for-net/issues/47265.
                pullRoleAssignment.Name = BicepFunction.CreateGuid(
                    containerRegistry.Id,
                    newImagePullIdentity.Id,
                    pullRoleAssignment.RoleDefinitionId);
                infrastructure.Add(pullRoleAssignment);
            }

            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = sandboxGroup.Id.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = sandboxGroup.Name.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("location", typeof(string)) { Value = sandboxGroup.Location.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput(AzureSandboxGroupResource.ImagePullIdentityClientIdOutputName, typeof(string))
            {
                Value = imagePullIdentityClientId
            });

            if (!sandboxResource.IsExisting())
            {
                AddSandboxGroupDeploymentPrincipalRoleAssignment(infrastructure, sandboxGroup);
            }
        }

        var resource = new AzureSandboxGroupResource(name, ConfigureInfrastructure);
        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        builder.AddAzureProvisioning();
        AzureSandboxCleanupResource.EnsureAdded(builder);
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);
        resource.DefaultContainerRegistry = CreateDefaultAzureContainerRegistry(builder, $"{name}-acr");
        var resourceBuilder = builder.AddResource(resource);
        resourceBuilder.WithCrossScopeAcrPullIdentity(
            identity => new AzureSandboxGroupAcrPullIdentityAnnotation(identity, isAspireManaged: true));
        return resourceBuilder;
    }

    /// <summary>
    /// Publishes the specified compute resource as an Azure sandbox container.
    /// </summary>
    /// <typeparam name="T">The compute resource type.</typeparam>
    /// <param name="builder">The compute resource builder.</param>
    /// <param name="sandboxGroup">The Azure sandbox group that hosts the resource.</param>
    /// <param name="options">The sandbox runtime options.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="sandboxGroup"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a configured option is invalid.</exception>
    /// <remarks>
    /// This method assigns the compute resource to <paramref name="sandboxGroup"/> and configures all sandbox-specific
    /// runtime options in one call.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("publishComputeResourceAsAzureSandbox", MethodName = "publishAsAzureSandbox")]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> PublishAsAzureSandbox<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureSandboxGroupResource> sandboxGroup,
        AzureSandboxOptions? options = null)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sandboxGroup);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var sandboxOptions = options ?? new AzureSandboxOptions();
        ValidateSandboxOptions(sandboxOptions);

        var copiedOptions = CopyAzureSandboxOptions(sandboxOptions);

        return builder
            .WithComputeEnvironment(sandboxGroup)
            .WithAnnotation(new AzureSandboxContainerOptionsAnnotation(copiedOptions), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Publishes the specified compute resource as an Azure sandbox container.
    /// </summary>
    /// <typeparam name="T">The compute resource type.</typeparam>
    /// <param name="builder">The compute resource builder.</param>
    /// <param name="sandboxGroup">The Azure sandbox group that hosts the resource.</param>
    /// <param name="configure">The callback that configures sandbox runtime options.</param>
    /// <returns>The resource builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/>, <paramref name="sandboxGroup"/>, or <paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a configured option is invalid.</exception>
    [AspireExportIgnore(Reason = "Use the AzureSandboxOptions overload from ATS.")]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> PublishAsAzureSandbox<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureSandboxGroupResource> sandboxGroup,
        Action<AzureSandboxOptions> configure)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sandboxGroup);
        ArgumentNullException.ThrowIfNull(configure);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var options = new AzureSandboxOptions();
        configure(options);

        return builder.PublishAsAzureSandbox(sandboxGroup, options);
    }

    /// <summary>
    /// Clears the managed identities explicitly configured for sandbox workloads.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Workload identities requested by individual compute resources are added to the sandbox group during
    /// deployment preparation. Image pulls require a separate user-assigned identity. Aspire creates one by
    /// default for new groups; callers can select one with <c>WithAcrPullIdentity</c> and must do so for existing groups.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithNoManagedIdentity(this IResourceBuilder<AzureSandboxGroupResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.WorkloadManagedIdentityType = ManagedServiceIdentityType.None;
        builder.Resource.WorkloadUserAssignedIdentities.Clear();
        return builder;
    }

    /// <summary>
    /// Configures a system-assigned managed identity for sandbox workloads.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Image pulls require a separate user-assigned identity. Aspire creates one by default for new groups;
    /// callers can select one with <c>WithAcrPullIdentity</c> and must do so for existing groups. Workload identities
    /// requested by individual compute resources may add more user-assigned identities.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithSystemAssignedIdentity(this IResourceBuilder<AzureSandboxGroupResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.WorkloadManagedIdentityType = ManagedServiceIdentityType.SystemAssigned;
        builder.Resource.WorkloadUserAssignedIdentities.Clear();
        return builder;
    }

    /// <summary>
    /// Adds a user-assigned managed identity for sandbox workloads.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <param name="identity">The user-assigned managed identity resource.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Image pulls require a separate user-assigned identity. Aspire creates one by default for new groups;
    /// callers can select one with <c>WithAcrPullIdentity</c> and must do so for existing groups.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithUserAssignedIdentity(
        this IResourceBuilder<AzureSandboxGroupResource> builder,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identity);

        builder.Resource.AddWorkloadUserAssignedIdentity(identity.Resource);
        return builder;
    }

    /// <summary>
    /// Configures the user-assigned managed identity that Azure Dev Compute uses to pull sandbox images
    /// from the configured Azure Container Registry.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <param name="identity">The user-assigned managed identity resource.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// The supplied identity must be attached to an existing sandbox group and have the <c>AcrPull</c>
    /// role on its registry. For sandbox groups created by Aspire, the identity is attached automatically,
    /// but the caller remains responsible for granting <c>AcrPull</c>.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithAcrPullIdentity(
        this IResourceBuilder<AzureSandboxGroupResource> builder,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identity);

        builder.Resource.References.Add(identity.Resource);
        return builder.WithAnnotation(
            new AzureSandboxGroupAcrPullIdentityAnnotation(identity.Resource),
            ResourceAnnotationMutationBehavior.Replace);
    }

    private static AzureSandboxOptions CopyAzureSandboxOptions(AzureSandboxOptions options)
    {
        return new AzureSandboxOptions
        {
            Tier = options.Tier,
            AutoSuspendEnabled = options.AutoSuspendEnabled,
            AutoSuspendInterval = options.AutoSuspendInterval,
            AutoSuspendMode = options.AutoSuspendMode,
            AutoDeleteEnabled = options.AutoDeleteEnabled,
            AutoDeleteInterval = options.AutoDeleteInterval,
            AutoDeleteTrigger = options.AutoDeleteTrigger,
            PublicEndpointReadyTimeout = options.PublicEndpointReadyTimeout,
            Endpoints = options.Endpoints?.Select(static endpoint => new AzureSandboxEndpointOptions
            {
                Name = endpoint.Name,
                Anonymous = endpoint.Anonymous
            }).ToArray()
        };
    }

    private static string GetAccessPolicyResourceName(AzureConnectorNamespaceConnectionResource connection, string name)
    {
        // Keep the parent and policy names readable while hashing the delimited pair so different
        // combinations cannot normalize to the same Bicep identifier.
        var identity = $"{connection.Name}\0{name}";
        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(identity)).ToString("x16", CultureInfo.InvariantCulture);
        return $"{connection.Name}-policy-{name}-{hash}";
    }

    private static IEnumerable<IResource> GetConnectorNamespaceChildren(AzureConnectorNamespaceResource connectorNamespace)
    {
        foreach (var connection in connectorNamespace.Connections)
        {
            yield return connection;

            foreach (var accessPolicy in connection.AccessPolicies)
            {
                yield return accessPolicy;
            }
        }

        foreach (var mcpServerConfig in connectorNamespace.McpServerConfigs)
        {
            yield return mcpServerConfig;
        }
    }

    private static void ValidateUniqueBicepIdentifier(
        IEnumerable<IResource> resources,
        string name,
        string resourceType,
        string connectorNamespaceName)
    {
        var bicepIdentifier = Infrastructure.NormalizeBicepIdentifier(name);
        if (resources.Any(resource =>
            string.Equals(
                Infrastructure.NormalizeBicepIdentifier(resource.Name),
                bicepIdentifier,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"{resourceType} resource '{name}' conflicts with an existing resource on Connector Namespace " +
                $"'{connectorNamespaceName}' after Bicep identifier normalization.");
        }
    }

    private static string GetValidatedAccessPolicyResourceName(
        AzureConnectorNamespaceConnectionResource connection,
        string name,
        string policyName)
    {
        var resourceName = GetAccessPolicyResourceName(connection, name);
        var bicepIdentifier = Infrastructure.NormalizeBicepIdentifier(resourceName);
        if (connection.AccessPolicies.Any(policy =>
            string.Equals(
                Infrastructure.NormalizeBicepIdentifier(policy.Name),
                bicepIdentifier,
                StringComparison.OrdinalIgnoreCase)))
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
            GetConnectorNamespaceChildren(connection.Parent),
            resourceName,
            "Access policy",
            connection.Parent.Name);

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

    private static InvalidOperationException CreateExistingSandboxGroupMissingAcrPullIdentityException(
        AzureSandboxGroupResource sandboxGroup)
        => new(
            $"Existing Azure sandbox group '{sandboxGroup.Name}' requires a user-assigned ACR pull identity. " +
            $"Call '{nameof(WithAcrPullIdentity)}' with an identity that is already attached to the sandbox group and has AcrPull on the configured registry.");

    private static void AddSandboxGroupDeploymentPrincipalRoleAssignment(AzureResourceInfrastructure infrastructure, SandboxGroup sandboxGroup)
    {
        var principalId = new ProvisioningParameter(AzureBicepResource.KnownParameters.PrincipalId, typeof(Guid));
        infrastructure.Add(principalId);

        // Sandbox deployment creates disk images, sandboxes, lifecycle settings, and public
        // ports through the Azure Dev Compute data-plane API after the sandbox group ARM
        // resource is provisioned. Model the deployment-principal grant in the sandbox
        // group's own Azure.Provisioning module, just like other Azure deployment targets
        // model environment-owned RBAC in their environment resource. The publish pipeline
        // wires these well-known principal parameters from the outer Azure environment,
        // while direct `aspire deploy` fills it from the current Azure principal.
        // https://learn.microsoft.com/azure/templates/microsoft.authorization/2022-04-01/roleassignments
        infrastructure.Add(new RoleAssignment($"{sandboxGroup.BicepIdentifier}_deploymentPrincipalDataOwner")
        {
            Name = BicepFunction.CreateGuid(
                sandboxGroup.Id,
                principalId,
                BicepFunction.GetSubscriptionResourceId("Microsoft.Authorization/roleDefinitions", SandboxGroupDataOwnerRoleId)),
            Scope = new IdentifierExpression(sandboxGroup.BicepIdentifier),
            PrincipalId = principalId,
            RoleDefinitionId = BicepFunction.GetSubscriptionResourceId("Microsoft.Authorization/roleDefinitions", SandboxGroupDataOwnerRoleId)
        });
    }

    private static void ValidateSandboxOptions(AzureSandboxOptions options)
    {
        if (!Enum.IsDefined(options.Tier))
        {
            throw new ArgumentException($"'{options.Tier}' is not a valid Azure sandbox tier.", nameof(options));
        }

        ValidateOptionalWholeSecondDuration(
            options.AutoSuspendInterval,
            nameof(AzureSandboxOptions.AutoSuspendInterval),
            TimeSpan.FromSeconds(int.MaxValue));
        ValidateOptionalEnum(options.AutoSuspendMode, nameof(AzureSandboxOptions.AutoSuspendMode));
        ValidateOptionalWholeSecondDuration(options.AutoDeleteInterval, nameof(AzureSandboxOptions.AutoDeleteInterval));
        ValidateOptionalEnum(options.AutoDeleteTrigger, nameof(AzureSandboxOptions.AutoDeleteTrigger));
        ValidateOptionalPositiveDuration(
            options.PublicEndpointReadyTimeout,
            nameof(AzureSandboxOptions.PublicEndpointReadyTimeout),
            TimeSpan.FromSeconds(int.MaxValue));

        if (options.AutoSuspendEnabled is null &&
            (options.AutoSuspendInterval is not null || options.AutoSuspendMode is not null))
        {
            throw new ArgumentException(
                $"{nameof(AzureSandboxOptions.AutoSuspendEnabled)} must be set when configuring auto-suspend interval or mode.",
                nameof(options));
        }

        if (options.AutoDeleteEnabled is null &&
            (options.AutoDeleteInterval is not null || options.AutoDeleteTrigger is not null))
        {
            throw new ArgumentException(
                $"{nameof(AzureSandboxOptions.AutoDeleteEnabled)} must be set when configuring auto-delete interval or trigger.",
                nameof(options));
        }

        if (options.Endpoints is null)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in options.Endpoints)
        {
            if (endpoint is null)
            {
                throw new ArgumentException("Endpoint options cannot contain null values.", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(endpoint.Name))
            {
                throw new ArgumentException("Endpoint option names cannot be empty.", nameof(options));
            }

            if (!names.Add(endpoint.Name))
            {
                throw new ArgumentException($"Endpoint option '{endpoint.Name}' is configured more than once.", nameof(options));
            }
        }
    }

    private static void ValidateOptionalWholeSecondDuration(TimeSpan? value, string paramName, TimeSpan? maximum = null)
    {
        if (value is null)
        {
            return;
        }

        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, "The value cannot be negative.");
        }

        if (value.Value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("The value must use whole-second precision.", paramName);
        }

        if (maximum is not null && value > maximum)
        {
            throw new ArgumentOutOfRangeException(paramName, $"The value cannot exceed {maximum}.");
        }
    }

    private static void ValidateOptionalPositiveDuration(TimeSpan? value, string paramName, TimeSpan? maximum = null)
    {
        if (value is not null && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, "The value must be positive.");
        }

        if (maximum is not null && value > maximum)
        {
            throw new ArgumentOutOfRangeException(paramName, $"The value cannot exceed {maximum}.");
        }
    }

    private static void ValidateOptionalEnum<TEnum>(TEnum? value, string paramName)
        where TEnum : struct, Enum
    {
        if (value is not null && !Enum.IsDefined(value.Value))
        {
            throw new ArgumentException($"'{value}' is not a valid {typeof(TEnum).Name} value.", paramName);
        }
    }

    private static void ApplyManagedServiceIdentity(
        ManagedServiceIdentity identity,
        AzureSandboxGroupResource resource,
        BicepValue<string> imagePullIdentityId,
        AzureResourceInfrastructure infrastructure)
    {
        identity.ManagedServiceIdentityType = resource.WorkloadManagedIdentityType switch
        {
            ManagedServiceIdentityType.None => ManagedServiceIdentityType.UserAssigned,
            ManagedServiceIdentityType.SystemAssigned => ManagedServiceIdentityType.SystemAssignedUserAssigned,
            _ => resource.WorkloadManagedIdentityType
        };
        var imagePullIdentityKey = BicepFunction.Interpolate($"{imagePullIdentityId}").Compile().ToString();
        identity.UserAssignedIdentities[imagePullIdentityKey] = new UserAssignedIdentityDetails();

        foreach (var userAssignedIdentity in resource.WorkloadUserAssignedIdentities)
        {
            var userAssignedIdentityIdParameter = userAssignedIdentity.Id.AsProvisioningParameter(infrastructure);
            var userAssignedIdentityId = BicepFunction.Interpolate($"{userAssignedIdentityIdParameter}").Compile().ToString();
            identity.UserAssignedIdentities[userAssignedIdentityId] = new UserAssignedIdentityDetails();
        }
    }

    private static AzureContainerRegistryResource CreateDefaultAzureContainerRegistry(IDistributedApplicationBuilder builder, string name)
    {
        var resource = new AzureContainerRegistryResource(name, ContainerRegistryInfrastructure.ConfigureContainerRegistry);
        if (builder.ExecutionContext.IsPublishMode)
        {
            builder.AddResource(resource)
                .WithAnnotation(new DefaultRoleAssignmentsAnnotation(new HashSet<RoleDefinition>()));
        }

        return resource;
    }
}
