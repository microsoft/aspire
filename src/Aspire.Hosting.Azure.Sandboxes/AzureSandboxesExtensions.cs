// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREPIPELINES003 // Container build options are required by the sandbox deployment target.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Sandboxes.Provisioning;
using Aspire.Hosting.Publishing;
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
    /// <summary>
    /// Adds an Azure connector namespace resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A resource builder for the connector namespace.</returns>
    /// <remarks>
    /// The connector namespace hosts connector connections and managed MCP server configs.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayResource> AddAzureConnectorGateway(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.AddAzureProvisioning();
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var gatewayResource = (AzureConnectorGatewayResource)infrastructure.AspireResource;
            var gateway = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = ConnectorGateway.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                infrastructure => new ConnectorGateway(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    Location = BicepFunction.GetResourceGroup().Location,
                    Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                });

            gateway.Identity.ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssigned;

            var connectionMap = new Dictionary<AzureConnectorGatewayConnectionResource, ConnectorGatewayConnection>();
            foreach (var connectionResource in gatewayResource.Connections)
            {
                var connection = new ConnectorGatewayConnection(Infrastructure.NormalizeBicepIdentifier(connectionResource.Name))
                {
                    Parent = gateway,
                    Name = connectionResource.ConnectionName,
                    DisplayName = connectionResource.DisplayName ?? connectionResource.ConnectionName,
                    ConnectorName = connectionResource.ConnectorName
                };
                infrastructure.Add(connection);
                connectionMap.Add(connectionResource, connection);
            }

            var gatewayIdentity = new MemberExpression(new IdentifierExpression(gateway.BicepIdentifier), "identity");
            foreach (var connectionResource in gatewayResource.Connections)
            {
                var connection = connectionMap[connectionResource];
                foreach (var accessPolicyResource in connectionResource.AccessPolicies)
                {
                    var accessPolicy = new ConnectorGatewayConnectionAccessPolicy(Infrastructure.NormalizeBicepIdentifier(accessPolicyResource.Name))
                    {
                        Parent = connection,
                        Name = accessPolicyResource.PolicyName,
                        Location = BicepFunction.GetResourceGroup().Location
                    };

                    switch (accessPolicyResource.Principal)
                    {
                        case AzureConnectorGatewayConnectionAccessPolicyPrincipal.GatewayManagedIdentity:
                            accessPolicy.Principal.Type = "ActiveDirectory";
                            accessPolicy.Principal.Identity.ObjectId = (BicepValue<string>)new MemberExpression(gatewayIdentity, "principalId");
                            accessPolicy.Principal.Identity.TenantId = (BicepValue<string>)new MemberExpression(gatewayIdentity, "tenantId");
                            break;
                        default:
                            throw new NotSupportedException($"Access policy principal '{accessPolicyResource.Principal}' is not supported.");
                    }

                    infrastructure.Add(accessPolicy);
                }
            }

            foreach (var configResource in gatewayResource.McpServerConfigs)
            {
                var config = new ConnectorGatewayMcpServerConfig(Infrastructure.NormalizeBicepIdentifier(configResource.Name))
                {
                    Parent = gateway,
                    Name = configResource.ConfigName,
                    Kind = "ManagedMcpServer"
                };

                if (!string.IsNullOrWhiteSpace(configResource.Description))
                {
                    config.Description = configResource.Description;
                }

                foreach (var connectorDefinition in configResource.Connectors)
                {
                    var connection = connectionMap[connectorDefinition.Connection];
                    if (!config.DependsOn.Contains(connection))
                    {
                        config.DependsOn.Add(connection);
                    }

                    var connector = new ConnectorGatewayMcpConnector
                    {
                        Name = connectorDefinition.Name,
                        ConnectionName = connectorDefinition.Connection.ConnectionName
                    };

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

                infrastructure.Add(config);
            }

            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = gateway.Id.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = gateway.Name.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("principalId", typeof(string)) { Value = (BicepValue<string>)new MemberExpression(gatewayIdentity, "principalId") });
            infrastructure.Add(new ProvisioningOutput("tenantId", typeof(string)) { Value = (BicepValue<string>)new MemberExpression(gatewayIdentity, "tenantId") });
        }

        var resource = new AzureConnectorGatewayResource(name, ConfigureInfrastructure);
        return builder.AddResource(resource);
    }

    /// <summary>
    /// Adds a connector connection to an Azure connector namespace.
    /// </summary>
    /// <param name="builder">The connector namespace resource builder.</param>
    /// <param name="name">The name of the Aspire resource.</param>
    /// <param name="connectorName">The connector catalog name.</param>
    /// <param name="displayName">The friendly display name shown for the connection.</param>
    /// <param name="connectionName">The Azure connector connection name. Defaults to <paramref name="name"/>.</param>
    /// <returns>A resource builder for the connector connection.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "This C# helper accepts endpoint references and trigger parameter DTOs that are not ATS-compatible.")]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayConnectionResource> AddConnection(
        this IResourceBuilder<AzureConnectorGatewayResource> builder,
        [ResourceName] string name,
        string connectorName,
        string? displayName = null,
        string? connectionName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        connectionName ??= name;

        var connection = new AzureConnectorGatewayConnectionResource(name, connectionName, connectorName, displayName, builder.Resource);
        builder.Resource.Connections.Add(connection);
        return builder.ApplicationBuilder.AddResource(connection);
    }

    /// <summary>
    /// Adds a managed MCP server config to an Azure connector namespace.
    /// </summary>
    /// <param name="builder">The connector namespace resource builder.</param>
    /// <param name="name">The name of the Aspire resource.</param>
    /// <param name="description">The description shown to MCP clients.</param>
    /// <param name="configName">The Azure MCP server config name. Defaults to <paramref name="name"/>.</param>
    /// <returns>A resource builder for the MCP server config.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayMcpServerConfigResource> AddMcpServerConfig(
        this IResourceBuilder<AzureConnectorGatewayResource> builder,
        [ResourceName] string name,
        string? description = null,
        string? configName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        configName ??= name;

        var config = new AzureConnectorGatewayMcpServerConfigResource(name, configName, description, builder.Resource);
        builder.Resource.McpServerConfigs.Add(config);
        return builder.ApplicationBuilder.AddResource(config);
    }

    /// <summary>
    /// Adds a connector trigger config that posts notifications to an Azure sandbox endpoint.
    /// </summary>
    /// <param name="builder">The connector connection resource builder.</param>
    /// <param name="name">The name of the Aspire resource.</param>
    /// <param name="operationName">The connector trigger operation name.</param>
    /// <param name="callbackEndpoint">The sandbox endpoint that receives trigger notifications.</param>
    /// <param name="callbackPath">The optional path appended to the sandbox endpoint URL.</param>
    /// <param name="parameters">The connector trigger parameters.</param>
    /// <param name="description">The trigger config description.</param>
    /// <param name="triggerName">The Azure trigger config name. Defaults to <paramref name="name"/>.</param>
    /// <returns>A resource builder for the connector trigger config.</returns>
    /// <remarks>
    /// Connector trigger configs depend on the public sandbox URL produced by the sandbox data-plane
    /// deployment. Aspire therefore provisions this resource in a late deploy step after the sandbox
    /// endpoint state is available, instead of including it in the connector namespace's first-pass
    /// Azure.Provisioning module.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "This C# helper accepts endpoint references and trigger parameter DTOs that are not ATS-compatible.")]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayTriggerConfigResource> AddTriggerConfig(
        this IResourceBuilder<AzureConnectorGatewayConnectionResource> builder,
        [ResourceName] string name,
        string operationName,
        EndpointReference callbackEndpoint,
        string? callbackPath = null,
        IEnumerable<AzureConnectorGatewayTriggerParameter>? parameters = null,
        string? description = null,
        string? triggerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(callbackEndpoint);

        triggerName ??= name;
        EnsureGatewayAccessPolicy(builder.Resource);

        var trigger = new AzureConnectorGatewayTriggerConfigResource(
            name,
            triggerName,
            operationName,
            callbackEndpoint,
            callbackPath,
            description,
            builder.Resource,
            parameters?.ToArray() ?? []);

        return builder.ApplicationBuilder.AddResource(trigger);
    }

    /// <summary>
    /// Adds a connector operation route to a managed MCP server config.
    /// </summary>
    /// <param name="builder">The MCP server config resource builder.</param>
    /// <param name="name">The connector catalog name to expose through this MCP server config.</param>
    /// <param name="connection">The connector connection backing this MCP route.</param>
    /// <param name="operationName">The connector operation name.</param>
    /// <param name="displayName">The display name shown for the operation.</param>
    /// <param name="description">The description shown for the operation.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayMcpServerConfigResource> WithConnector(
        this IResourceBuilder<AzureConnectorGatewayMcpServerConfigResource> builder,
        string name,
        IResourceBuilder<AzureConnectorGatewayConnectionResource> connection,
        string operationName,
        string? displayName = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        var connector = builder.Resource.Connectors.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        if (connector is null)
        {
            connector = new AzureConnectorGatewayMcpConnectorDefinition(name, connection.Resource);
            builder.Resource.Connectors.Add(connector);
        }
        else if (!ReferenceEquals(connector.Connection, connection.Resource))
        {
            throw new InvalidOperationException($"Connector '{name}' is already registered with a different connection resource.");
        }

        connector.Operations.Add(new AzureConnectorGatewayMcpOperationDefinition(operationName, displayName, description));
        return builder;
    }

    private static void EnsureGatewayAccessPolicy(AzureConnectorGatewayConnectionResource connection)
    {
        const string accessPolicyName = "gateway-acl";
        if (connection.AccessPolicies.Any(policy =>
            string.Equals(policy.PolicyName, accessPolicyName, StringComparison.Ordinal) &&
            policy.Principal == AzureConnectorGatewayConnectionAccessPolicyPrincipal.GatewayManagedIdentity))
        {
            return;
        }

        // Connector event triggers require the connector namespace's managed identity to
        // be explicitly authorized on the connection. The ARM child resource shape is:
        //   Microsoft.Web/connectorGateways/{gateway}/connections/{connection}/accessPolicies/{name}
        // with principal.type = ActiveDirectory and identity = { objectId, tenantId }.
        // The objectId/tenantId come from the gateway's system-assigned identity output,
        // so this policy can stay in the first-pass gateway Azure.Provisioning module.
        connection.AccessPolicies.Add(
            AzureConnectorGatewayConnectionAccessPolicyResource.CreateGatewayManagedIdentityPolicy(
                $"{connection.Name}-gateway-acl",
                accessPolicyName,
                connection));
    }

    /// <summary>
    /// Adds an Azure Container Apps sandbox group resource to the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A resource builder for the sandbox group.</returns>
    /// <remarks>
    /// Use <see cref="WithRoleAssignments{T}(IResourceBuilder{T}, IResourceBuilder{AzureSandboxGroupResource}, AzureSandboxGroupBuiltInRole[])"/>
    /// to grant an application resource access to the sandbox group.
    /// </remarks>
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

            AddSandboxGroupDeploymentPrincipalRoleAssignment(infrastructure, sandboxGroup);
            AddSandboxGroupPrincipalRoleAssignments(infrastructure, sandboxGroup, sandboxResource);
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
    [AspireExport("publishComputeResourceAsAzureSandbox", MethodName = "publishAsSandbox")]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> PublishAsSandbox<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureSandboxGroupResource> sandboxGroup,
        AzureSandboxOptions? options = null)
        where T : IResource, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sandboxGroup);

        var sandboxOptions = options ?? new AzureSandboxOptions();
        ValidateSandboxOptions(sandboxOptions);
        var copiedOptions = CopyAzureSandboxOptions(sandboxOptions);

        return builder
            .WithComputeEnvironment(sandboxGroup)
            .WithContainerBuildOptions(static context =>
            {
                // ADC creates disk images from registry images. Buildx's default output can push an
                // OCI image index with provenance attestations, which ADC currently treats as a
                // ready disk image but boots without a usable root filesystem. Force a single
                // Docker-format linux/amd64 image for resources published to sandboxes.
                context.Destination = ContainerImageDestination.Registry;
                context.ImageFormat = ContainerImageFormat.Docker;
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            })
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
    public static IResourceBuilder<T> PublishAsSandbox<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureSandboxGroupResource> sandboxGroup,
        Action<AzureSandboxOptions> configure)
        where T : IResource, IComputeResource
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AzureSandboxOptions();
        configure(options);

        return builder.PublishAsSandbox(sandboxGroup, options);
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

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Azure Container Apps sandbox group resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Azure sandbox group resource.</param>
    /// <param name="roles">The built-in sandbox group roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    [AspireExportIgnore(Reason = "AzureSandboxGroupBuiltInRole is not compatible with ATS. Use the AzureSandboxGroupRole-based overload instead.")]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureSandboxGroupResource> target,
        params AzureSandboxGroupBuiltInRole[] roles)
        where T : IResource
    {
        return builder.WithRoleAssignments(target, AzureSandboxGroupBuiltInRole.GetBuiltInRoleName, roles);
    }

    /// <summary>
    /// Assigns the specified roles to an Azure connector gateway, granting its system-assigned managed identity
    /// the necessary permissions on the target Azure Container Apps sandbox group resource.
    /// </summary>
    /// <param name="builder">The Azure connector gateway resource whose system-assigned managed identity receives the role assignments.</param>
    /// <param name="target">The target Azure sandbox group resource.</param>
    /// <param name="roles">The built-in sandbox group roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{AzureConnectorGatewayResource}"/> with the applied role assignments.</returns>
    [AspireExportIgnore(Reason = "AzureSandboxGroupBuiltInRole is not compatible with ATS.")]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureConnectorGatewayResource> WithRoleAssignments(
        this IResourceBuilder<AzureConnectorGatewayResource> builder,
        IResourceBuilder<AzureSandboxGroupResource> target,
        params AzureSandboxGroupBuiltInRole[] roles)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(target);

        IReadOnlySet<AzureSandboxGroupBuiltInRole> roleSet = roles is null ? new HashSet<AzureSandboxGroupBuiltInRole>() : roles.ToHashSet();
        target.Resource.RoleAssignmentPrincipals.Add(new AzureSandboxGroupRoleAssignmentPrincipal(
            builder.Resource.Name,
            builder.Resource.PrincipalId,
            RoleManagementPrincipalType.ServicePrincipal,
            roleSet));

        return builder;
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Azure Container Apps sandbox group resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Azure sandbox group resource.</param>
    /// <param name="roles">The Azure sandbox group roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <exception cref="ArgumentException">Thrown when a role value is not a valid <see cref="AzureSandboxGroupRole"/> value.</exception>
    [AspireExport("withSandboxGroupRoleAssignments")]
    internal static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureSandboxGroupResource> target,
        params AzureSandboxGroupRole[] roles)
        where T : IResource
    {
        if (roles is null || roles.Length == 0)
        {
            return builder.WithRoleAssignments(target, Array.Empty<AzureSandboxGroupBuiltInRole>());
        }

        var builtInRoles = new AzureSandboxGroupBuiltInRole[roles.Length];
        for (var i = 0; i < roles.Length; i++)
        {
            builtInRoles[i] = roles[i] switch
            {
                AzureSandboxGroupRole.SandboxGroupDataOwner => AzureSandboxGroupBuiltInRole.SandboxGroupDataOwner,
                _ => throw new ArgumentException($"'{roles[i]}' is not a valid {nameof(AzureSandboxGroupRole)} value.", nameof(roles))
            };
        }

        return builder.WithRoleAssignments(target, builtInRoles);
    }

    private static AzureSandboxOptions CopyAzureSandboxOptions(AzureSandboxOptions options)
    {
        return new AzureSandboxOptions
        {
            Cpu = options.Cpu,
            Memory = options.Memory,
            Disk = options.Disk,
            AutoSuspendEnabled = options.AutoSuspendEnabled,
            AutoSuspendInterval = options.AutoSuspendInterval,
            AutoSuspendMode = options.AutoSuspendMode,
            AutoDeleteEnabled = options.AutoDeleteEnabled,
            AutoDeleteIntervalInDays = options.AutoDeleteIntervalInDays,
            AutoDeleteIntervalInSeconds = options.AutoDeleteIntervalInSeconds,
            AutoDeleteTrigger = options.AutoDeleteTrigger,
            EgressProxyEnabled = options.EgressProxyEnabled,
            EgressTrafficInspection = options.EgressTrafficInspection,
            PublicEndpointReadyTimeoutSeconds = options.PublicEndpointReadyTimeoutSeconds,
            Endpoints = options.Endpoints?.Select(static endpoint => new AzureSandboxEndpointOptions
            {
                Name = endpoint.Name,
                Anonymous = endpoint.Anonymous
            }).ToArray()
        };
    }

    private static void AddSandboxGroupDeploymentPrincipalRoleAssignment(AzureResourceInfrastructure infrastructure, SandboxGroup sandboxGroup)
    {
        var role = AzureSandboxGroupBuiltInRole.SandboxGroupDataOwner;
        var roleId = role.ToString();
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
                BicepFunction.GetSubscriptionResourceId("Microsoft.Authorization/roleDefinitions", roleId)),
            Scope = new IdentifierExpression(sandboxGroup.BicepIdentifier),
            PrincipalType = principalType,
            PrincipalId = principalId,
            RoleDefinitionId = BicepFunction.GetSubscriptionResourceId("Microsoft.Authorization/roleDefinitions", roleId)
        });
    }

    private static void AddSandboxGroupPrincipalRoleAssignments(
        AzureResourceInfrastructure infrastructure,
        SandboxGroup sandboxGroup,
        AzureSandboxGroupResource sandboxResource)
    {
        foreach (var principal in sandboxResource.RoleAssignmentPrincipals)
        {
            if (principal.Roles.Count == 0)
            {
                continue;
            }

            var principalId = principal.PrincipalId.AsProvisioningParameter(
                infrastructure,
                $"{Infrastructure.NormalizeBicepIdentifier(principal.Name)}_principalId");

            foreach (var role in principal.Roles)
            {
                var roleId = role.ToString();
                var roleName = AzureSandboxGroupBuiltInRole.GetBuiltInRoleName(role);
                infrastructure.Add(new RoleAssignment($"{sandboxGroup.BicepIdentifier}_{Infrastructure.NormalizeBicepIdentifier(principal.Name)}_{Infrastructure.NormalizeBicepIdentifier(roleName)}")
                {
                    Name = BicepFunction.CreateGuid(
                        sandboxGroup.Id,
                        principalId,
                        BicepFunction.GetSubscriptionResourceId("Microsoft.Authorization/roleDefinitions", roleId)),
                    Scope = new IdentifierExpression(sandboxGroup.BicepIdentifier),
                    PrincipalType = principal.PrincipalType,
                    PrincipalId = principalId,
                    RoleDefinitionId = BicepFunction.GetSubscriptionResourceId("Microsoft.Authorization/roleDefinitions", roleId)
                });
            }
        }
    }

    private static void ValidateSandboxOptions(AzureSandboxOptions options)
    {
        ValidateOptionalQuantity(options.Cpu, nameof(AzureSandboxOptions.Cpu));
        ValidateOptionalQuantity(options.Memory, nameof(AzureSandboxOptions.Memory));
        ValidateOptionalQuantity(options.Disk, nameof(AzureSandboxOptions.Disk));
        ValidateOptionalNonNegative(options.AutoSuspendInterval, nameof(AzureSandboxOptions.AutoSuspendInterval));
        ValidateOptionalAllowedValue(options.AutoSuspendMode, nameof(AzureSandboxOptions.AutoSuspendMode), "Memory", "Disk", "None");
        ValidateOptionalNonNegative(options.AutoDeleteIntervalInDays, nameof(AzureSandboxOptions.AutoDeleteIntervalInDays));
        ValidateOptionalNonNegative(options.AutoDeleteIntervalInSeconds, nameof(AzureSandboxOptions.AutoDeleteIntervalInSeconds));
        ValidateOptionalAllowedValue(options.AutoDeleteTrigger, nameof(AzureSandboxOptions.AutoDeleteTrigger), "AfterSuspend", "AfterCreation");
        ValidateOptionalAllowedValue(options.EgressTrafficInspection, nameof(AzureSandboxOptions.EgressTrafficInspection), "Legacy", "Partial", "Full", "None");
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

    private static void ValidateOptionalQuantity(string? value, string paramName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The quantity cannot be empty.", paramName);
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
