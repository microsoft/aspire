// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIREAZURE001

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Sandboxes.Provisioning;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Azure Container Apps sandbox resources to the application model.
/// </summary>
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class AzureSandboxesExtensions
{
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayResource> AddAzureConnectorGateway(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.AddAzureProvisioning();
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var gatewayResource = (AzureConnectorGatewayResource)infrastructure.AspireResource;
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

            var connectionMap = new Dictionary<AzureConnectorGatewayConnectionResource, ConnectorGatewayConnection>();
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
                        Location = BicepFunction.GetResourceGroup().Location
                    };

                    accessPolicy.Principal.Type = "ActiveDirectory";
                    if (accessPolicyResource.UsesGatewayManagedIdentity)
                    {
                        accessPolicy.Principal.Identity.ObjectId =
                            (BicepValue<string>)new MemberExpression(gatewayIdentity, "principalId");
                        accessPolicy.Principal.Identity.TenantId =
                            (BicepValue<string>)new MemberExpression(gatewayIdentity, "tenantId");
                    }
                    else if (accessPolicyResource.IdentityResource is { } identityResource)
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
            infrastructure.Add(new ProvisioningOutput("principalId", typeof(string))
            {
                Value = (BicepValue<string>)new MemberExpression(gatewayIdentity, "principalId")
            });
            infrastructure.Add(new ProvisioningOutput("tenantId", typeof(string))
            {
                Value = (BicepValue<string>)new MemberExpression(gatewayIdentity, "tenantId")
            });
        }

        return builder.AddResource(new AzureConnectorGatewayResource(name, ConfigureInfrastructure));
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
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayConnectionResource> AddConnection(
        this IResourceBuilder<AzureConnectorGatewayResource> builder,
        [ResourceName] string name,
        string connectorName,
        AzureConnectorGatewayConnectionOptions? options = null)
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

        var connection = new AzureConnectorGatewayConnectionResource(
            name,
            connectionName,
            connectorName,
            options?.DisplayName,
            builder.Resource);
        builder.Resource.Connections.Add(connection);
        return builder.ApplicationBuilder.AddResource(connection);
    }

    /// <summary>
    /// Marks a Connector Namespace connection as an existing Azure resource.
    /// </summary>
    /// <param name="builder">The connection resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("asExistingConnectorGatewayConnection", MethodName = "asExisting")]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayConnectionResource> AsExisting(
        this IResourceBuilder<AzureConnectorGatewayConnectionResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!string.IsNullOrWhiteSpace(builder.Resource.DisplayName))
        {
            throw new InvalidOperationException(
                $"Connector connection '{builder.Resource.Name}' configures a display name and cannot be marked as existing.");
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
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayConnectionResource> WithAccessPolicy(
        this IResourceBuilder<AzureConnectorGatewayConnectionResource> builder,
        [ResourceName] string name,
        AzureConnectorGatewayAccessPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ObjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TenantId);

        var policyName = options.PolicyName ?? name;
        ValidateConnectorResourceName(policyName, nameof(options));
        if (builder.Resource.AccessPolicies.Any(policy =>
            string.Equals(policy.PolicyName, policyName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Access policy '{policyName}' is already registered on connector connection '{builder.Resource.Name}'.");
        }

        builder.Resource.AccessPolicies.Add(new AzureConnectorGatewayConnectionAccessPolicyResource(
            name,
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
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayConnectionResource> WithIdentityAccessPolicy(
        this IResourceBuilder<AzureConnectorGatewayConnectionResource> builder,
        [ResourceName] string name,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity,
        string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(identity);

        policyName ??= name;
        ValidateConnectorResourceName(policyName, nameof(policyName));
        if (builder.Resource.AccessPolicies.Any(policy =>
            string.Equals(policy.PolicyName, policyName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Access policy '{policyName}' is already registered on connector connection '{builder.Resource.Name}'.");
        }

        builder.Resource.AccessPolicies.Add(
            AzureConnectorGatewayConnectionAccessPolicyResource.CreateUserAssignedIdentityPolicy(
                name,
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
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayMcpServerConfigResource> AddMcpServerConfig(
        this IResourceBuilder<AzureConnectorGatewayResource> builder,
        [ResourceName] string name,
        AzureConnectorGatewayMcpServerConfigOptions? options = null)
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

        var config = new AzureConnectorGatewayMcpServerConfigResource(
            name,
            configName,
            options?.Description,
            builder.Resource);
        builder.Resource.McpServerConfigs.Add(config);
        return builder.ApplicationBuilder.AddResource(config);
    }

    /// <summary>
    /// Marks a managed MCP server configuration as an existing Azure resource.
    /// </summary>
    /// <param name="builder">The MCP server configuration resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("asExistingConnectorGatewayMcpServerConfig", MethodName = "asExisting")]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayMcpServerConfigResource> AsExisting(
        this IResourceBuilder<AzureConnectorGatewayMcpServerConfigResource> builder)
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
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayMcpServerConfigResource> WithConnector(
        this IResourceBuilder<AzureConnectorGatewayMcpServerConfigResource> builder,
        string connectorName,
        IResourceBuilder<AzureConnectorGatewayConnectionResource> connection,
        AzureConnectorGatewayMcpConnectorOptions options)
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

        if (options.Operations is null || options.Operations.Length == 0)
        {
            throw new ArgumentException(
                "At least one connector operation must be explicitly allow-listed.",
                nameof(options));
        }

        if (builder.Resource.Connectors.Any(connector =>
            string.Equals(connector.Name, connectorName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Connector route '{connectorName}' is already registered on MCP server configuration '{builder.Resource.Name}'.");
        }

        var operationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectorDefinition = new AzureConnectorGatewayMcpConnectorDefinition(
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

            connectorDefinition.Operations.Add(new AzureConnectorGatewayMcpOperationDefinition(
                operation.Name,
                operation.DisplayName,
                operation.Description));
        }

        builder.Resource.Connectors.Add(connectorDefinition);
        return builder;
    }

    /// <summary>
    /// Adds a Connector Namespace trigger that delivers connector events to an Azure sandbox endpoint.
    /// </summary>
    /// <param name="builder">The connector connection resource builder.</param>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="operationName">The connector trigger operation ID.</param>
    /// <param name="callbackEndpoint">The external Azure sandbox endpoint that receives trigger notifications.</param>
    /// <param name="options">The optional trigger configuration.</param>
    /// <returns>A resource builder for the trigger configuration.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the connection is existing, the callback endpoint is not external, or the physical trigger name is already registered.
    /// </exception>
    /// <remarks>
    /// The trigger is provisioned after the sandbox endpoint exists. The integration grants the
    /// Connector Namespace managed identity access to the connection and adds that identity to the
    /// sandbox port's Microsoft Entra allow-list. The callback remains non-anonymous. Existing
    /// connections are rejected because adding a trigger would otherwise mutate the connection by
    /// implicitly provisioning a new access policy.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayTriggerConfigResource> AddTriggerConfig(
        this IResourceBuilder<AzureConnectorGatewayConnectionResource> builder,
        [ResourceName] string name,
        string operationName,
        EndpointReference callbackEndpoint,
        AzureConnectorGatewayTriggerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(callbackEndpoint);

        if (builder.Resource.IsExisting)
        {
            throw new InvalidOperationException(
                $"Existing connector connection '{builder.Resource.Name}' is read-only and cannot create a trigger because trigger provisioning requires a new connection access policy.");
        }

        var triggerName = options?.TriggerName ?? name;
        ValidateConnectorResourceName(triggerName, nameof(options));
        ValidateTriggerParameters(options?.Parameters);
        if (builder.Resource.Parent.TriggerConfigs.Any(trigger =>
            string.Equals(trigger.TriggerName, triggerName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Trigger configuration '{triggerName}' is already registered on Connector Namespace '{builder.Resource.Parent.Name}'.");
        }

        var callbackResource = callbackEndpoint.Resource;
        var callbackEndpointAnnotation = callbackResource.Annotations
            .OfType<EndpointAnnotation>()
            .LastOrDefault(annotation => string.Equals(
                annotation.Name,
                callbackEndpoint.EndpointName,
                StringComparison.Ordinal));
        if (callbackEndpointAnnotation?.IsExternal != true)
        {
            throw new InvalidOperationException(
                $"Connector trigger callback endpoint '{callbackEndpoint.EndpointName}' on resource '{callbackResource.Name}' must be external.");
        }

        var trigger = new AzureConnectorGatewayTriggerConfigResource(
            name,
            triggerName,
            operationName,
            callbackEndpoint,
            options?.CallbackPath,
            options?.Description,
            builder.Resource,
            options?.Parameters ?? []);

        EnsureGatewayAccessPolicy(builder.Resource);
        if (!callbackResource.Annotations.OfType<AzureConnectorGatewayEndpointAuthorizationAnnotation>().Any(annotation =>
            string.Equals(annotation.EndpointName, callbackEndpoint.EndpointName, StringComparison.Ordinal) &&
            ReferenceEquals(annotation.ConnectorGateway, builder.Resource.Parent)))
        {
            callbackResource.Annotations.Add(new AzureConnectorGatewayEndpointAuthorizationAnnotation(
                callbackEndpoint.EndpointName,
                builder.Resource.Parent));
        }

        var triggerBuilder = builder.ApplicationBuilder.AddResource(trigger)
            .WithRelationship(callbackResource, "Callback");
        builder.Resource.Parent.TriggerConfigs.Add(trigger);
        return triggerBuilder;
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

        builder.AddAzureProvisioning();
        AzureSandboxCleanupResource.EnsureAdded(builder);
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var sandboxResource = (AzureSandboxGroupResource)infrastructure.AspireResource;
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
                        Location = BicepFunction.GetResourceGroup().Location,
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };
                    ApplyManagedServiceIdentity(resource.Identity, sandboxResource, infrastructure);
                    return resource;
                });

            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = sandboxGroup.Id.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = sandboxGroup.Name.ToBicepExpression() });

            if (!sandboxResource.IsExisting())
            {
                AddSandboxGroupDeploymentPrincipalRoleAssignment(infrastructure, sandboxGroup);
            }
        }

        var resource = new AzureSandboxGroupResource(name, ConfigureInfrastructure)
        {
            DefaultContainerRegistry = CreateDefaultAzureContainerRegistry(builder, $"{name}-acr")
        };

        return builder.AddResource(resource);
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
        where T : IResource, IComputeResource
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
        where T : IResource, IComputeResource
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
    /// Configures the Azure sandbox group to use no managed identity.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithNoManagedIdentity(this IResourceBuilder<AzureSandboxGroupResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.ManagedIdentityType = ManagedServiceIdentityType.None;
        builder.Resource.UserAssignedIdentities.Clear();
        return builder;
    }

    /// <summary>
    /// Configures the Azure sandbox group to use a system-assigned managed identity.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithSystemAssignedIdentity(this IResourceBuilder<AzureSandboxGroupResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.ManagedIdentityType = ManagedServiceIdentityType.SystemAssigned;
        builder.Resource.UserAssignedIdentities.Clear();
        return builder;
    }

    /// <summary>
    /// Configures the Azure sandbox group to use a user-assigned managed identity.
    /// </summary>
    /// <param name="builder">The sandbox group resource builder.</param>
    /// <param name="identity">The user-assigned managed identity resource.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureSandboxGroupResource> WithUserAssignedIdentity(
        this IResourceBuilder<AzureSandboxGroupResource> builder,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identity);

        builder.Resource.ManagedIdentityType = ManagedServiceIdentityType.UserAssigned;
        builder.Resource.UserAssignedIdentities.Add(identity.Resource);
        return builder;
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
            AutoDeleteIntervalInDays = options.AutoDeleteIntervalInDays,
            AutoDeleteIntervalInSeconds = options.AutoDeleteIntervalInSeconds,
            AutoDeleteTrigger = options.AutoDeleteTrigger,
            PublicEndpointReadyTimeoutSeconds = options.PublicEndpointReadyTimeoutSeconds,
            Endpoints = options.Endpoints?.Select(static endpoint => new AzureSandboxEndpointOptions
            {
                Name = endpoint.Name,
                Anonymous = endpoint.Anonymous
            }).ToArray()
        };
    }

    private static void EnsureGatewayAccessPolicy(AzureConnectorGatewayConnectionResource connection)
    {
        const string accessPolicyName = "gateway-acl";
        if (connection.AccessPolicies.Any(policy =>
            string.Equals(policy.PolicyName, accessPolicyName, StringComparison.OrdinalIgnoreCase) &&
            policy.UsesGatewayManagedIdentity))
        {
            return;
        }

        // Connector event subscriptions run as the Connector Namespace system-assigned identity.
        // The connection access policy is required even when the connection itself is OAuth-authorized.
        // https://github.com/Azure/Connectors/blob/main/plugin/skills/aca-sandboxes/references/trigger-setup.md
        connection.AccessPolicies.Add(
            AzureConnectorGatewayConnectionAccessPolicyResource.CreateGatewayManagedIdentityPolicy(
                $"{connection.Name}-gateway-acl",
                accessPolicyName,
                connection));
    }

    private static void ValidateTriggerParameters(AzureConnectorGatewayTriggerParameter[]? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            if (parameter is null)
            {
                throw new ArgumentException("Trigger parameters cannot contain null values.", nameof(parameters));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(parameter.Name);
            ArgumentNullException.ThrowIfNull(parameter.Value);
            if (!names.Add(parameter.Name))
            {
                throw new ArgumentException(
                    $"Trigger parameter '{parameter.Name}' is configured more than once.",
                    nameof(parameters));
            }
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

    private static void AddSandboxGroupDeploymentPrincipalRoleAssignment(AzureResourceInfrastructure infrastructure, SandboxGroup sandboxGroup)
    {
        var principalId = new ProvisioningParameter(AzureBicepResource.KnownParameters.UserPrincipalId, typeof(Guid));
        var principalType = new ProvisioningParameter(AzureBicepResource.KnownParameters.PrincipalType, typeof(string));
        infrastructure.Add(principalId);
        infrastructure.Add(principalType);

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
            PrincipalType = principalType,
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

        ValidateOptionalNonNegative(options.AutoSuspendInterval, nameof(AzureSandboxOptions.AutoSuspendInterval));
        ValidateOptionalAllowedValue(options.AutoSuspendMode, nameof(AzureSandboxOptions.AutoSuspendMode), "Memory", "Disk", "None");
        ValidateOptionalNonNegative(options.AutoDeleteIntervalInDays, nameof(AzureSandboxOptions.AutoDeleteIntervalInDays));
        ValidateOptionalNonNegative(options.AutoDeleteIntervalInSeconds, nameof(AzureSandboxOptions.AutoDeleteIntervalInSeconds));
        ValidateOptionalAllowedValue(options.AutoDeleteTrigger, nameof(AzureSandboxOptions.AutoDeleteTrigger), "AfterSuspend", "AfterCreation");
        ValidateOptionalPositive(options.PublicEndpointReadyTimeoutSeconds, nameof(AzureSandboxOptions.PublicEndpointReadyTimeoutSeconds));

        if (options.Endpoints is null)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
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

    private static void ValidateOptionalPositive(int? value, string paramName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, "The value must be positive.");
        }
    }

    private static void ValidateOptionalNonNegative(int? value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, "The value cannot be negative.");
        }
    }

    private static void ValidateOptionalNonNegative(long? value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, "The value cannot be negative.");
        }
    }

    private static void ValidateOptionalAllowedValue(string? value, string paramName, params string[] allowedValues)
    {
        if (value is null)
        {
            return;
        }

        foreach (var allowedValue in allowedValues)
        {
            if (string.Equals(value, allowedValue, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new ArgumentException($"The value '{value}' is not supported. Supported values: {string.Join(", ", allowedValues)}.", paramName);
    }

    private static void ApplyManagedServiceIdentity(ManagedServiceIdentity identity, AzureSandboxGroupResource resource, AzureResourceInfrastructure infrastructure)
    {
        if (resource.ManagedIdentityType == ManagedServiceIdentityType.None && resource.UserAssignedIdentities.Count == 0)
        {
            return;
        }

        identity.ManagedServiceIdentityType = resource.ManagedIdentityType;

        foreach (var userAssignedIdentity in resource.UserAssignedIdentities)
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
