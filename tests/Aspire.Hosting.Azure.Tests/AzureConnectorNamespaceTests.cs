// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.ConnectorNamespace.Provisioning;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Tests;

public class AzureConnectorNamespaceTests
{
    private static readonly MethodInfo s_polyglotWithReferenceMethod = typeof(ResourceBuilderExtensions)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(m => m.Name == nameof(ResourceBuilderExtensions.WithReference)
            && m.IsGenericMethodDefinition
            && m.GetParameters() is { Length: 5 } parameters
            && parameters[1].ParameterType == typeof(IResourceBuilder<IResource>));

    [Fact]
    public void AddAzureConnectorNamespaceDoesNotEnableTargetedRoleAssignments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureConnectorNamespace("gateway");

        using var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>();

        Assert.False(options.Value.SupportsTargetedRoleAssignments);
    }

    [Fact]
    public async Task WithReferenceAddsConnectorSdkConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorNamespace("gateway")
            .AddConnection(
                "outlook",
                "office365",
                new AzureConnectorNamespaceConnectionOptions { ConnectionName = "office365-outlook" });
        var worker = builder.AddContainer("worker", "fake")
            .WithReference(connection);

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            worker.Resource,
            DistributedApplicationOperation.Publish,
            TestServiceProvider.Instance);

        Assert.Equal(2, environment.Count);
        Assert.Equal("{gateway.outputs.name}", environment["outlook__connectorGatewayName"]);
        Assert.Equal("office365-outlook", environment["outlook__connectionName"]);
    }

    [Fact]
    public async Task PolyglotWithReferenceUsesConfigurationOverrideAndPhysicalExistingConnectionName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorNamespace("gateway")
            .AddConnection(
                "outlook",
                "office365",
                new AzureConnectorNamespaceConnectionOptions { ConnectionName = "existing-outlook" })
            .AsExisting();
        var worker = builder.AddContainer("worker", "fake");

        InvokeWithReference(worker, connection, connectionName: "mail");

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            worker.Resource,
            DistributedApplicationOperation.Publish,
            TestServiceProvider.Instance);

        Assert.Equal(2, environment.Count);
        Assert.Equal("{gateway.outputs.name}", environment["mail__connectorGatewayName"]);
        Assert.Equal("existing-outlook", environment["mail__connectionName"]);
    }

    [Theory]
    [InlineData(true, null, "Optional references are not supported for Connector Namespace connections.")]
    [InlineData(false, "mail", "Named service references are not supported for Connector Namespace connections.")]
    public void PolyglotWithReferenceRejectsUnsupportedOptions(
        bool optional,
        string? name,
        string expectedMessage)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorNamespace("gateway")
            .AddConnection("outlook", "office365");
        var worker = builder.AddContainer("worker", "fake");

        var exception = Assert.Throws<TargetInvocationException>(
            () => InvokeWithReference(worker, connection, optional: optional, name: name));
        var dispatchException = Assert.IsType<TargetInvocationException>(exception.InnerException);
        var innerException = Assert.IsType<InvalidOperationException>(dispatchException.InnerException);

        Assert.Equal(expectedMessage, innerException.Message);
    }

    [Fact]
    public void WithReferenceRejectsEmptyConfigurationOverride()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorNamespace("gateway")
            .AddConnection("outlook", "office365");
        var worker = builder.AddContainer("worker", "fake");

        var exception = Assert.Throws<ArgumentException>(
            () => worker.WithReference(connection, string.Empty));

        Assert.Equal("connectionName", exception.ParamName);
    }

    [Fact]
    public async Task AddAzureConnectorNamespaceResourcesGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("location");
        var connection = gateway.AddConnection(
            "office365",
            "office365",
            new AzureConnectorNamespaceConnectionOptions
            {
                ConnectionName = "office365-outlook",
                DisplayName = "Office 365 Outlook"
            });
        connection.WithAccessPolicy(
            "worker-access",
            new AzureConnectorNamespaceAccessPolicyOptions
            {
                PolicyName = "worker-acl",
                ObjectId = "11111111-1111-1111-1111-111111111111",
                TenantId = "22222222-2222-2222-2222-222222222222"
            });
        connection.WithIdentityAccessPolicy(
            "worker-identity-access",
            builder.AddAzureUserAssignedIdentity("worker-identity"),
            policyName: "worker-identity-acl");
        var mcp = gateway.AddMcpServerConfig(
            "outlook-mcp",
            new AzureConnectorNamespaceMcpServerConfigOptions
            {
                ConfigName = "outlook-tools",
                Description = "Allow-listed Outlook tools."
            });
        mcp.WithConnector(
            "office365",
            connection,
            new AzureConnectorNamespaceMcpConnectorOptions
            {
                Description = "Read-only Outlook operations.",
                Operations =
                [
                    new AzureConnectorNamespaceMcpOperationOptions
                    {
                        Name = "GetEmailsV3",
                        Description = "Reads recent emails."
                    }
                ]
            })
            .WithAccessPolicy(
                "developer-access",
                new AzureConnectorNamespaceMcpAccessPolicyOptions
                {
                    ObjectId = "33333333-3333-3333-3333-333333333333",
                    TenantId = "22222222-2222-2222-2222-222222222222",
                    PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
                });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Same(gateway.Resource, connection.Resource.Parent);
        Assert.Same(gateway.Resource, mcp.Resource.Parent);
        Assert.Equal(
            ManifestPublishingCallbackAnnotation.Ignore,
            Assert.Single(connection.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()));
        Assert.Equal(
            ManifestPublishingCallbackAnnotation.Ignore,
            Assert.Single(mcp.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()));
        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(model, gateway.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ExistingConnectorNamespaceChildrenGenerateExistingBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway")
            .PublishAsExisting("existing-gateway", "existing-rg");
        gateway.AddConnection("office365", "office365", new AzureConnectorNamespaceConnectionOptions
        {
            ConnectionName = "existing-connection"
        }).AsExisting();
        gateway.AddMcpServerConfig("mcp", new AzureConnectorNamespaceMcpServerConfigOptions
        {
            ConfigName = "existing-mcp"
        }).AsExisting();
        gateway.AddConnection("sharepoint", "sharepointonline")
            .WithAccessPolicy(
                "reader",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    ObjectId = "11111111-1111-1111-1111-111111111111",
                    TenantId = "22222222-2222-2222-2222-222222222222"
                });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(model, gateway.Resource);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public void ManagedMcpServerRequiresExplicitOperationAllowList()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
        var connection = gateway.AddConnection("office365", "office365");
        var mcp = gateway.AddMcpServerConfig("outlook-mcp");

        var exception = Assert.Throws<ArgumentException>(() => mcp.WithConnector(
            "office365",
            connection,
            new AzureConnectorNamespaceMcpConnectorOptions()));

        Assert.Equal("At least one connector operation must be explicitly allow-listed. (Parameter 'options')", exception.Message);
    }

    [Fact]
    public async Task ManagedMcpServerRequiresConnectorBeforeGeneratingBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
        gateway.AddMcpServerConfig("outlook-mcp");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.GetManifestWithBicep(model, gateway.Resource));

        Assert.Equal(
            "MCP server configuration 'outlook-mcp' requires a connector. " +
            "Call 'WithConnector' before generating the Azure deployment.",
            exception.Message);
    }

    [Fact]
    public async Task ManagedMcpServerRequiresAccessPolicyBeforeGeneratingBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
        var connection = gateway.AddConnection("office365", "office365");
        gateway.AddMcpServerConfig("outlook-mcp")
            .WithConnector(
                "office365",
                connection,
                new AzureConnectorNamespaceMcpConnectorOptions
                {
                    Operations = [new AzureConnectorNamespaceMcpOperationOptions { Name = "GetEmailsV3" }]
                });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AzureManifestUtils.GetManifestWithBicep(model, gateway.Resource));

        Assert.Equal(
            "MCP server configuration 'outlook-mcp' requires an access policy. " +
            "Call 'WithAccessPolicy' before generating the Azure deployment.",
            exception.Message);
    }

    [Fact]
    public void ConnectorConnectionsRejectDuplicateAzureNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
        gateway.AddConnection(
            "office365",
            "office365",
            new AzureConnectorNamespaceConnectionOptions { ConnectionName = "shared-connection" });

        var exception = Assert.Throws<InvalidOperationException>(() => gateway.AddConnection(
            "sharepoint",
            "sharepointonline",
            new AzureConnectorNamespaceConnectionOptions { ConnectionName = "shared-connection" }));

        Assert.Equal(
            "Connector connection 'shared-connection' is already registered on Connector Namespace 'gateway'.",
            exception.Message);
        Assert.Single(gateway.Resource.Connections);
    }

    [Fact]
    public void ConnectorMcpServerConfigsRejectDuplicateAzureNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
        gateway.AddMcpServerConfig(
            "first-mcp",
            new AzureConnectorNamespaceMcpServerConfigOptions { ConfigName = "shared-mcp" });

        var exception = Assert.Throws<InvalidOperationException>(() => gateway.AddMcpServerConfig(
            "second-mcp",
            new AzureConnectorNamespaceMcpServerConfigOptions { ConfigName = "shared-mcp" }));

        Assert.Equal(
            "MCP server configuration 'shared-mcp' is already registered on Connector Namespace 'gateway'.",
            exception.Message);
        Assert.Single(gateway.Resource.McpServerConfigs);
    }

    [Fact]
    public async Task ConnectorBicepIdentifiersAreCollisionResistant()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("location");
        var firstConnection = gateway.AddConnection("abcdefghijklmnop-a", "office365");
        var secondConnection = gateway.AddConnection("abcdefghijklmnop-b", "sharepointonline");
        var mcp = gateway.AddMcpServerConfig("abcdefghijklmnop-c");
        mcp.WithConnector(
            "office365",
            firstConnection,
            new AzureConnectorNamespaceMcpConnectorOptions
            {
                Operations = [new AzureConnectorNamespaceMcpOperationOptions { Name = "GetEmailsV3" }]
            })
            .WithAccessPolicy(
                "reader",
                new AzureConnectorNamespaceMcpAccessPolicyOptions
                {
                    ObjectId = "11111111-1111-1111-1111-111111111111",
                    TenantId = "22222222-2222-2222-2222-222222222222",
                    PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
                });

        var gatewayBicepIdentifier = ConnectorNamespaceBicepIdentifiers.Gateway;
        Assert.NotEqual(firstConnection.Resource.BicepIdentifier, secondConnection.Resource.BicepIdentifier);
        Assert.NotEqual(secondConnection.Resource.BicepIdentifier, mcp.Resource.BicepIdentifier);
        Assert.All(
            ["location", "outputs", "principalId", "tenantId"],
            reservedName => Assert.False(string.Equals(
                reservedName,
                gatewayBicepIdentifier,
                StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("connectorGateway", gatewayBicepIdentifier);
        Assert.StartsWith("connectorConnection_location_abcdefghijklmnop_", firstConnection.Resource.BicepIdentifier, StringComparison.Ordinal);
        Assert.StartsWith("connectorMcpServer_location_abcdefghijklmnop_", mcp.Resource.BicepIdentifier, StringComparison.Ordinal);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (_, bicep) = await AzureManifestUtils.GetManifestWithBicep(model, gateway.Resource);

        Assert.Contains($"resource {gatewayBicepIdentifier} ", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectorNamespaceResourceNamesRemainUniqueAfterNormalizationAndTruncation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var firstGateway = builder.AddAzureConnectorNamespace("abcdefghijklmnopqrstuvwx-1");
        var secondGateway = builder.AddAzureConnectorNamespace("abcdefghijklmnopqrstuvwx1");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (_, firstBicep) = await AzureManifestUtils.GetManifestWithBicep(model, firstGateway.Resource);
        var (_, secondBicep) = await AzureManifestUtils.GetManifestWithBicep(model, secondGateway.Resource);

        Assert.Contains(
            "name: 'abcdefghijk${uniqueString(resourceGroup().id, 'abcdefghijklmnopqrstuvwx-1')}'",
            firstBicep,
            StringComparison.Ordinal);
        Assert.Contains(
            "name: 'abcdefghijk${uniqueString(resourceGroup().id, 'abcdefghijklmnopqrstuvwx1')}'",
            secondBicep,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedMcpServerSupportsOnlyOneConnector()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
        var office365 = gateway.AddConnection("office365", "office365");
        var sharepoint = gateway.AddConnection("sharepoint", "sharepointonline");
        var mcp = gateway.AddMcpServerConfig("mcp");
        var options = new AzureConnectorNamespaceMcpConnectorOptions
        {
            Operations = [new AzureConnectorNamespaceMcpOperationOptions { Name = "GetItem" }]
        };

        mcp.WithConnector("mail", office365, options);

        var exception = Assert.Throws<InvalidOperationException>(
            () => mcp.WithConnector("files", sharepoint, options));

        Assert.Equal(
            "MCP server configuration 'mcp' already has a connector. " +
            "The current Connector Namespace preview supports one connector per MCP server configuration.",
            exception.Message);
        Assert.Single(mcp.Resource.Connectors);
    }

    [Fact]
    public void ExistingMcpServerConfigRejectsAccessPolicy()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var mcp = builder.AddAzureConnectorNamespace("gateway")
            .AddMcpServerConfig("mcp")
            .AsExisting();

        var exception = Assert.Throws<InvalidOperationException>(() => mcp.WithAccessPolicy(
            "reader",
            new AzureConnectorNamespaceMcpAccessPolicyOptions
            {
                ObjectId = "11111111-1111-1111-1111-111111111111",
                TenantId = "22222222-2222-2222-2222-222222222222",
                PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
            }));

        Assert.Equal(
            "Existing MCP server configuration 'mcp' is read-only and cannot create an access policy.",
            exception.Message);
        Assert.Empty(mcp.Resource.AccessPolicies);
    }

    [Fact]
    public void McpServerConfigCannotBecomeExistingAfterAccessPolicyRegistered()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var mcp = builder.AddAzureConnectorNamespace("gateway")
            .AddMcpServerConfig("mcp")
            .WithAccessPolicy(
                "reader",
                new AzureConnectorNamespaceMcpAccessPolicyOptions
                {
                    ObjectId = "11111111-1111-1111-1111-111111111111",
                    TenantId = "22222222-2222-2222-2222-222222222222",
                    PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
                });

        var exception = Assert.Throws<InvalidOperationException>(mcp.AsExisting);

        Assert.Equal(
            "MCP server configuration 'mcp' configures access policies and cannot be marked as existing.",
            exception.Message);
        Assert.False(mcp.Resource.IsExisting);
    }

    [Theory]
    [InlineData(
        "not-a-guid",
        "22222222-2222-2222-2222-222222222222",
        AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User,
        "The MCP access policy object ID must be a valid GUID. (Parameter 'options')")]
    [InlineData(
        "11111111-1111-1111-1111-111111111111",
        "not-a-guid",
        AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User,
        "The MCP access policy tenant ID must be a valid GUID. (Parameter 'options')")]
    [InlineData(
        "11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222",
        (AzureConnectorNamespaceMcpAccessPolicyPrincipalType)0,
        "'0' is not a supported MCP access policy principal type. (Parameter 'options')")]
    public void McpAccessPolicyRejectsInvalidOptions(
        string objectId,
        string tenantId,
        AzureConnectorNamespaceMcpAccessPolicyPrincipalType principalType,
        string expectedMessage)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var mcp = builder.AddAzureConnectorNamespace("gateway")
            .AddMcpServerConfig("mcp");

        var exception = Assert.Throws<ArgumentException>(() => mcp.WithAccessPolicy(
            "reader",
            new AzureConnectorNamespaceMcpAccessPolicyOptions
            {
                ObjectId = objectId,
                TenantId = tenantId,
                PrincipalType = principalType
            }));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Empty(mcp.Resource.AccessPolicies);
    }

    [Fact]
    public void McpAccessPoliciesRejectDuplicateResourceNamesAndPrincipals()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var mcp = builder.AddAzureConnectorNamespace("gateway")
            .AddMcpServerConfig("mcp")
            .WithAccessPolicy(
                "reader",
                new AzureConnectorNamespaceMcpAccessPolicyOptions
                {
                    ObjectId = "11111111-1111-1111-1111-111111111111",
                    TenantId = "22222222-2222-2222-2222-222222222222",
                    PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
                });

        var duplicateResourceException = Assert.Throws<InvalidOperationException>(() => mcp.WithAccessPolicy(
            "reader",
            new AzureConnectorNamespaceMcpAccessPolicyOptions
            {
                ObjectId = "33333333-3333-3333-3333-333333333333",
                TenantId = "22222222-2222-2222-2222-222222222222",
                PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.Group
            }));
        var duplicatePrincipalException = Assert.Throws<InvalidOperationException>(() => mcp.WithAccessPolicy(
            "other-reader",
            new AzureConnectorNamespaceMcpAccessPolicyOptions
            {
                ObjectId = "11111111-1111-1111-1111-111111111111",
                TenantId = "22222222-2222-2222-2222-222222222222",
                PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
            }));

        Assert.Equal(
            "Access policy resource 'reader' is already registered on MCP server configuration 'mcp'.",
            duplicateResourceException.Message);
        Assert.Equal(
            "An access policy for principal '11111111-1111-1111-1111-111111111111' is already registered on MCP server configuration 'mcp'.",
            duplicatePrincipalException.Message);
        Assert.Single(mcp.Resource.AccessPolicies);
    }

    [Fact]
    public void ConnectorConnectionCannotBecomeExistingAfterAccessPolicyRegistered()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorNamespace("gateway")
            .AddConnection("office365", "office365")
            .WithAccessPolicy(
                "reader",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    ObjectId = "11111111-1111-1111-1111-111111111111",
                    TenantId = "22222222-2222-2222-2222-222222222222"
                });

        var exception = Assert.Throws<InvalidOperationException>(connection.AsExisting);

        Assert.Equal(
            "Connector connection 'office365' configures access policies and cannot be marked as existing.",
            exception.Message);
        Assert.False(connection.Resource.IsExisting);
    }

    [Theory]
    [InlineData(
        "not-a-guid",
        "22222222-2222-2222-2222-222222222222",
        "The connection access policy object ID must be a valid GUID. (Parameter 'options')")]
    [InlineData(
        "11111111-1111-1111-1111-111111111111",
        "not-a-guid",
        "The connection access policy tenant ID must be a valid GUID. (Parameter 'options')")]
    public void ConnectionAccessPolicyRejectsInvalidPrincipalIds(
        string objectId,
        string tenantId,
        string expectedMessage)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorNamespace("gateway")
            .AddConnection("office365", "office365");

        var exception = Assert.Throws<ArgumentException>(() => connection.WithAccessPolicy(
            "reader",
            new AzureConnectorNamespaceAccessPolicyOptions
            {
                ObjectId = objectId,
                TenantId = tenantId
            }));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Empty(connection.Resource.AccessPolicies);
    }

    [Fact]
    public void AccessPoliciesIdentifyEmptyPrincipalIdOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
        var connection = gateway.AddConnection("office365", "office365");
        var mcp = gateway.AddMcpServerConfig("mcp");

        var connectionException = Assert.Throws<ArgumentException>(() => connection.WithAccessPolicy(
            "reader",
            new AzureConnectorNamespaceAccessPolicyOptions
            {
                ObjectId = string.Empty,
                TenantId = "22222222-2222-2222-2222-222222222222"
            }));
        var mcpException = Assert.Throws<ArgumentException>(() => mcp.WithAccessPolicy(
            "reader",
            new AzureConnectorNamespaceMcpAccessPolicyOptions
            {
                ObjectId = "11111111-1111-1111-1111-111111111111",
                TenantId = string.Empty,
                PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.User
            }));

        Assert.Equal(nameof(AzureConnectorNamespaceAccessPolicyOptions.ObjectId), connectionException.ParamName);
        Assert.Equal(nameof(AzureConnectorNamespaceMcpAccessPolicyOptions.TenantId), mcpException.ParamName);
        Assert.Empty(connection.Resource.AccessPolicies);
        Assert.Empty(mcp.Resource.AccessPolicies);
    }

    [Fact]
    public void ExistingConnectorConnectionRejectsExplicitAccessPolicy()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorNamespace("gateway")
            .AddConnection("office365", "office365")
            .AsExisting();

        var exception = Assert.Throws<InvalidOperationException>(() => connection.WithAccessPolicy(
            "reader",
            new AzureConnectorNamespaceAccessPolicyOptions
            {
                ObjectId = "11111111-1111-1111-1111-111111111111",
                TenantId = "22222222-2222-2222-2222-222222222222"
            }));

        Assert.Equal(
            "Existing connector connection 'office365' is read-only and cannot create an access policy.",
            exception.Message);
        Assert.Empty(connection.Resource.AccessPolicies);
    }

    [Fact]
    public void ConnectorAccessPolicyResourceNamesIncludeParentConnection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
        var office365 = gateway.AddConnection("office365", "office365")
            .WithAccessPolicy(
                "reader",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    ObjectId = "11111111-1111-1111-1111-111111111111",
                    TenantId = "22222222-2222-2222-2222-222222222222"
                });
        var sharepoint = gateway.AddConnection("sharepoint", "sharepointonline")
            .WithAccessPolicy(
                "reader",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    ObjectId = "33333333-3333-3333-3333-333333333333",
                    TenantId = "22222222-2222-2222-2222-222222222222"
                });
        var compoundParentName = gateway.AddConnection("ab-c", "office365")
            .WithAccessPolicy(
                "de",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    ObjectId = "44444444-4444-4444-4444-444444444444",
                    TenantId = "22222222-2222-2222-2222-222222222222"
                });
        var compoundPolicyName = gateway.AddConnection("ab", "sharepointonline")
            .WithAccessPolicy(
                "c-de",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    ObjectId = "55555555-5555-5555-5555-555555555555",
                    TenantId = "22222222-2222-2222-2222-222222222222"
                });

        Assert.StartsWith(
            "connectorAccessPolicy_gateway_office365_reader_",
            Assert.Single(office365.Resource.AccessPolicies).Name,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "connectorAccessPolicy_gateway_sharepoint_reader_",
            Assert.Single(sharepoint.Resource.AccessPolicies).Name,
            StringComparison.Ordinal);
        Assert.NotEqual(
            Assert.Single(compoundParentName.Resource.AccessPolicies).Name,
            Assert.Single(compoundPolicyName.Resource.AccessPolicies).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConnectorAccessPolicyRequiresUniqueBicepIdentifier(bool useIdentityPolicy)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorNamespace("gateway")
            .AddConnection("office365", "office365")
            .WithAccessPolicy(
                "reader",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    PolicyName = "first-policy",
                    ObjectId = "11111111-1111-1111-1111-111111111111",
                    TenantId = "22222222-2222-2222-2222-222222222222"
                });

        InvalidOperationException exception;
        if (useIdentityPolicy)
        {
            exception = Assert.Throws<InvalidOperationException>(() => connection.WithIdentityAccessPolicy(
                "reader",
                builder.AddAzureUserAssignedIdentity("reader-identity"),
                "second-policy"));
        }
        else
        {
            exception = Assert.Throws<InvalidOperationException>(() => connection.WithAccessPolicy(
                "reader",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    PolicyName = "second-policy",
                    ObjectId = "33333333-3333-3333-3333-333333333333",
                    TenantId = "22222222-2222-2222-2222-222222222222"
                }));
        }

        Assert.Equal(
            "Access policy resource 'reader' is already registered on connector connection 'office365'.",
            exception.Message);
        Assert.Single(connection.Resource.AccessPolicies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConnectorAccessPolicyResourceNamesAreCollisionResistant(bool useIdentityPolicy)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var connection = builder.AddAzureConnectorNamespace("gateway")
            .AddConnection("office365", "office365")
            .WithAccessPolicy(
                "reader-access",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    PolicyName = "first-policy",
                    ObjectId = "11111111-1111-1111-1111-111111111111",
                    TenantId = "22222222-2222-2222-2222-222222222222"
                });

        if (useIdentityPolicy)
        {
            connection.WithIdentityAccessPolicy(
                "reader_access",
                builder.AddAzureUserAssignedIdentity("reader-identity"),
                "second-policy");
        }
        else
        {
            connection.WithAccessPolicy(
                "reader_access",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    PolicyName = "second-policy",
                    ObjectId = "33333333-3333-3333-3333-333333333333",
                    TenantId = "22222222-2222-2222-2222-222222222222"
                });
        }

        Assert.Collection(
            connection.Resource.AccessPolicies,
            first => Assert.StartsWith("connectorAccessPolicy_gateway_office365_reader_access_", first.Name, StringComparison.Ordinal),
            second => Assert.StartsWith("connectorAccessPolicy_gateway_office365_reader_access_", second.Name, StringComparison.Ordinal));
        Assert.NotEqual(
            connection.Resource.AccessPolicies[0].BicepIdentifier,
            connection.Resource.AccessPolicies[1].BicepIdentifier);
    }

    [Fact]
    public void ConnectorNamespaceBicepIdentifiersAreDistinctAcrossDeclarationKinds()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
        var connection = gateway.AddConnection("mail-connection", "office365")
            .WithAccessPolicy(
                "reader",
                new AzureConnectorNamespaceAccessPolicyOptions
                {
                    ObjectId = "11111111-1111-1111-1111-111111111111",
                    TenantId = "22222222-2222-2222-2222-222222222222"
                });
        var mcp = gateway.AddMcpServerConfig("mail-config");
        mcp.WithAccessPolicy(
            "reader",
            new AzureConnectorNamespaceMcpAccessPolicyOptions
            {
                ObjectId = "11111111-1111-1111-1111-111111111111",
                TenantId = "22222222-2222-2222-2222-222222222222",
                PrincipalType = AzureConnectorNamespaceMcpAccessPolicyPrincipalType.Group
            });

        var identifiers = new[]
        {
            ConnectorNamespaceBicepIdentifiers.Gateway,
            connection.Resource.BicepIdentifier,
            Assert.Single(connection.Resource.AccessPolicies).BicepIdentifier,
            mcp.Resource.BicepIdentifier,
            Assert.Single(mcp.Resource.AccessPolicies).BicepIdentifier
        };

        Assert.Equal(identifiers.Length, identifiers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static IResourceBuilder<TDestination> InvokeWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName = null,
        bool optional = false,
        string? name = null)
        where TDestination : IResourceWithEnvironment
    {
        return (IResourceBuilder<TDestination>)s_polyglotWithReferenceMethod
            .MakeGenericMethod(typeof(TDestination))
            .Invoke(null, [builder, source, connectionName, optional, name])!;
    }
}
