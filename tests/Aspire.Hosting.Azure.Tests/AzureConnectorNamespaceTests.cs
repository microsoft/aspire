// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Azure.Provisioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Tests;

public class AzureConnectorNamespaceTests
{
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
    public async Task AddAzureConnectorNamespaceResourcesGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
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

    [Theory]
    [InlineData("mail", "mail")]
    public void ConnectorConnectionsRejectDuplicateBicepIdentifier(string firstName, string secondName)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
        gateway.AddConnection(firstName, "office365");

        var exception = Assert.Throws<InvalidOperationException>(
            () => gateway.AddConnection(secondName, "sharepointonline"));

        Assert.Equal(
            $"Connector connection resource '{secondName}' generates a duplicate Bicep identifier on Connector Namespace 'gateway'.",
            exception.Message);
        Assert.Single(gateway.Resource.Connections);
    }

    [Fact]
    public void ConnectorMcpServerConfigsRejectDuplicateBicepIdentifier()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("gateway");
        gateway.AddMcpServerConfig("mail");

        var exception = Assert.Throws<InvalidOperationException>(
            () => gateway.AddMcpServerConfig("mail"));

        Assert.Equal(
            "MCP server configuration resource 'mail' generates a duplicate Bicep identifier on Connector Namespace 'gateway'.",
            exception.Message);
        Assert.Single(gateway.Resource.McpServerConfigs);
    }

    [Fact]
    public void ConnectorChildBicepIdentifiersAreCollisionResistant()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorNamespace("location");
        var firstConnection = gateway.AddConnection("abcdefghijklmnop-a", "office365");
        var secondConnection = gateway.AddConnection("abcdefghijklmnop-b", "sharepointonline");
        var mcp = gateway.AddMcpServerConfig("abcdefghijklmnop-c");

        Assert.NotEqual(firstConnection.Resource.BicepIdentifier, secondConnection.Resource.BicepIdentifier);
        Assert.NotEqual(secondConnection.Resource.BicepIdentifier, mcp.Resource.BicepIdentifier);
        Assert.All(
            ["location", "outputs", "principalId", "tenantId"],
            reservedName => Assert.False(string.Equals(
                reservedName,
                firstConnection.Resource.BicepIdentifier,
                StringComparison.OrdinalIgnoreCase)));
        Assert.StartsWith("connectorConnection_location_abcdefghijklmnop_", firstConnection.Resource.BicepIdentifier, StringComparison.Ordinal);
        Assert.StartsWith("connectorMcpServer_location_abcdefghijklmnop_", mcp.Resource.BicepIdentifier, StringComparison.Ordinal);
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

        var identifiers = new[]
        {
            Infrastructure.NormalizeBicepIdentifier(gateway.Resource.Name),
            connection.Resource.BicepIdentifier,
            Assert.Single(connection.Resource.AccessPolicies).BicepIdentifier,
            mcp.Resource.BicepIdentifier
        };

        Assert.Equal(identifiers.Length, identifiers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
