// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Azure.Tests;

public class BicepUtilitiesTests
{
    [Fact]
    public async Task SetParametersTranslatesParametersToARMCompatibleJsonParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var bicep0 = builder.AddBicepTemplateString("bicep0", "param name string")
               .WithParameter("name", "david");

        var parameters = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters, bicep0.Resource);

        Assert.Single(parameters);
        Assert.Equal("david", parameters["name"]?["value"]?.ToString());
    }

    [Fact]
    public async Task SetParametersTranslatesCompatibleParameterTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("foo", "image")
            .WithHttpEndpoint()
            .WithEndpoint("http", e =>
            {
                e.AllocatedEndpoint = new(e, "localhost", 1023);
            });

        builder.Configuration["Parameters:param"] = "paramValue";

        var connectionStringResource = builder.CreateResourceBuilder(
            new ResourceWithConnectionString("A", "connection string"));

        var param = builder.AddParameter("param");

        var bicep0 = builder.AddBicepTemplateString("bicep0", "param name string")
               .WithParameter("name", "john")
               .WithParameter("age", () => 20)
               .WithParameter("values", ["a", "b", "c"])
               .WithParameter("conn", connectionStringResource)
               .WithParameter("jsonObj", new JsonObject { ["key"] = "value" })
               .WithParameter("param", param)
               .WithParameter("expr", ReferenceExpression.Create($"{param.Resource}/1"))
               .WithParameter("endpoint", container.GetEndpoint("http"));

        var parameters = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters, bicep0.Resource);

        Assert.Equal(8, parameters.Count);
        Assert.Equal("john", parameters["name"]?["value"]?.ToString());
        Assert.Equal(20, parameters["age"]?["value"]?.GetValue<int>());
        Assert.Equal(["a", "b", "c"], parameters["values"]?["value"]?.AsArray()?.Select(v => v?.ToString()) ?? []);
        Assert.Equal("connection string", parameters["conn"]?["value"]?.ToString());
        Assert.Equal("value", parameters["jsonObj"]?["value"]?["key"]?.ToString());
        Assert.Equal("paramValue", parameters["param"]?["value"]?.ToString());
        Assert.Equal("paramValue/1", parameters["expr"]?["value"]?.ToString());
        Assert.Equal("http://localhost:1023", parameters["endpoint"]?["value"]?.ToString());

        // We don't yet process relationships set via the callbacks
        // so we don't see the testResource2 nor exe1
        Assert.True(bicep0.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Collection(relationships.DistinctBy(r => (r.Resource, r.Type)),
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Equal(connectionStringResource.Resource, r.Resource);
            },
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Equal(param.Resource, r.Resource);
            },
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Equal(container.Resource, r.Resource);
            });
    }

    [Fact]
    public async Task ResourceWithTheSameBicepTemplateAndParametersHaveTheSameCheckSum()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var bicep0 = builder.AddBicepTemplateString("bicep0", "param name string")
                       .WithParameter("name", "david")
                       .WithParameter("age", () => 20)
                       .WithParameter("values", ["a", "b", "c"])
                       .WithParameter("jsonObj", new JsonObject { ["key"] = "value" });

        var bicep1 = builder.AddBicepTemplateString("bicep1", "param name string")
                       .WithParameter("name", "david")
                       .WithParameter("age", () => 20)
                       .WithParameter("values", ["a", "b", "c"])
                       .WithParameter("jsonObj", new JsonObject { ["key"] = "value" });

        var parameters0 = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters0, bicep0.Resource);
        var checkSum0 = BicepUtilities.GetChecksum(bicep0.Resource, parameters0, null);

        var parameters1 = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters1, bicep1.Resource);
        var checkSum1 = BicepUtilities.GetChecksum(bicep1.Resource, parameters1, null);

        Assert.Equal(checkSum0, checkSum1);
    }

    [Fact]
    public async Task ResourceWithSameTemplateButDifferentParametersHaveDifferentChecksums()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var bicep0 = builder.AddBicepTemplateString("bicep0", "param name string")
                       .WithParameter("name", "david")
                       .WithParameter("age", () => 20)
                       .WithParameter("values", ["a", "b", "c"]);

        var bicep1 = builder.AddBicepTemplateString("bicep1", "param name string")
                       .WithParameter("name", "david")
                       .WithParameter("age", () => 20)
                       .WithParameter("values", ["a", "b", "c"])
                       .WithParameter("jsonObj", new JsonObject { ["key"] = "value" });

        var parameters0 = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters0, bicep0.Resource);
        var checkSum0 = BicepUtilities.GetChecksum(bicep0.Resource, parameters0, null);

        var parameters1 = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters1, bicep1.Resource);
        var checkSum1 = BicepUtilities.GetChecksum(bicep1.Resource, parameters1, null);

        Assert.NotEqual(checkSum0, checkSum1);
    }

    [Fact]
    public async Task ResourceWithDifferentScopeHaveDifferentChecksums()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var bicep0 = builder.AddBicepTemplateString("bicep0", "param name string")
                       .WithParameter("key", "value");
        bicep0.Resource.Scope = new("rg0");

        var bicep1 = builder.AddBicepTemplateString("bicep1", "param name string")
                       .WithParameter("key", "value");
        bicep1.Resource.Scope = new("rg1");

        var parameters0 = new JsonObject();
        var scope0 = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters0, bicep0.Resource);
        await BicepUtilities.SetScopeAsync(scope0, bicep0.Resource);
        var checkSum0 = BicepUtilities.GetChecksum(bicep0.Resource, parameters0, scope0);

        var parameters1 = new JsonObject();
        var scope1 = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters1, bicep1.Resource);
        await BicepUtilities.SetScopeAsync(scope1, bicep1.Resource);
        var checkSum1 = BicepUtilities.GetChecksum(bicep1.Resource, parameters1, scope1);

        Assert.NotEqual(checkSum0, checkSum1);
    }

    [Fact]
    public async Task ResourceWithSameScopeHaveSameChecksums()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var bicep0 = builder.AddBicepTemplateString("bicep0", "param name string")
                       .WithParameter("key", "value");
        bicep0.Resource.Scope = new("rg0");

        var bicep1 = builder.AddBicepTemplateString("bicep1", "param name string")
                       .WithParameter("key", "value");
        bicep1.Resource.Scope = new("rg0");

        var parameters0 = new JsonObject();
        var scope0 = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters0, bicep0.Resource);
        await BicepUtilities.SetScopeAsync(scope0, bicep0.Resource);
        var checkSum0 = BicepUtilities.GetChecksum(bicep0.Resource, parameters0, scope0);

        var parameters1 = new JsonObject();
        var scope1 = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters1, bicep1.Resource);
        await BicepUtilities.SetScopeAsync(scope1, bicep1.Resource);
        var checkSum1 = BicepUtilities.GetChecksum(bicep1.Resource, parameters1, scope1);

        Assert.Equal(checkSum0, checkSum1);
    }

    [Fact]
    public void GetChecksum_ReturnsSameChecksum_ForSameInputs()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep1 = builder.AddBicepTemplateString("test1", "param name string").Resource;
        var bicep2 = builder.AddBicepTemplateString("test2", "param name string").Resource;
        
        var parameters = new JsonObject
        {
            ["param1"] = new JsonObject { ["value"] = "value1" }
        };

        // Act
        var checksum1 = BicepUtilities.GetChecksum(bicep1, parameters, null);
        var checksum2 = BicepUtilities.GetChecksum(bicep2, parameters, null);

        // Assert
        Assert.Equal(checksum1, checksum2);
    }

    [Fact]
    public void GetChecksum_ReturnsDifferentChecksum_ForDifferentParameters()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        
        var parameters1 = new JsonObject
        {
            ["param1"] = new JsonObject { ["value"] = "value1" }
        };
        
        var parameters2 = new JsonObject
        {
            ["param1"] = new JsonObject { ["value"] = "value2" }
        };

        // Act
        var checksum1 = BicepUtilities.GetChecksum(bicep, parameters1, null);
        var checksum2 = BicepUtilities.GetChecksum(bicep, parameters2, null);

        // Assert
        Assert.NotEqual(checksum1, checksum2);
    }

    [Fact]
    public void GetChecksum_ReturnsDifferentChecksum_ForDifferentScope()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        
        var parameters = new JsonObject
        {
            ["param1"] = new JsonObject { ["value"] = "value1" }
        };
        
        var scope1 = new JsonObject { ["resourceGroup"] = "rg1" };
        var scope2 = new JsonObject { ["resourceGroup"] = "rg2" };

        // Act
        var checksum1 = BicepUtilities.GetChecksum(bicep, parameters, scope1);
        var checksum2 = BicepUtilities.GetChecksum(bicep, parameters, scope2);

        // Assert
        Assert.NotEqual(checksum1, checksum2);
    }

    [Theory]
    [InlineData("value1", "value1", true)]
    [InlineData("value1", "value2", false)]
    [InlineData(null, null, true)]
    [InlineData("value1", null, false)]
    [InlineData(null, "value1", false)]
    public void GetChecksum_ConsistentBehavior_ForParameterComparisons(string? value1, string? value2, bool shouldEqual)
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        
        var parameters1 = new JsonObject
        {
            ["param1"] = new JsonObject { ["value"] = value1 }
        };
        
        var parameters2 = new JsonObject
        {
            ["param1"] = new JsonObject { ["value"] = value2 }
        };

        // Act
        var checksum1 = BicepUtilities.GetChecksum(bicep, parameters1, null);
        var checksum2 = BicepUtilities.GetChecksum(bicep, parameters2, null);

        // Assert
        if (shouldEqual)
        {
            Assert.Equal(checksum1, checksum2);
        }
        else
        {
            Assert.NotEqual(checksum1, checksum2);
        }
    }

    [Fact]
    public async Task SetParametersAsync_IncludesAllParametersWhenSkipDynamicValuesIsFalse()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        bicep.Parameters["normalParam"] = "normalValue";
        bicep.Parameters[AzureBicepResource.KnownParameters.PrincipalId] = "someId";
        bicep.Parameters[AzureBicepResource.KnownParameters.Location] = "someLocation";
        
        var parameters = new JsonObject();

        // Act
        await BicepUtilities.SetParametersAsync(parameters, bicep);

        // Assert
        Assert.Equal(3, parameters.Count);
        Assert.True(parameters.ContainsKey("normalParam"));
        Assert.True(parameters.ContainsKey(AzureBicepResource.KnownParameters.PrincipalId));
        Assert.True(parameters.ContainsKey(AzureBicepResource.KnownParameters.Location));
    }

    [Fact]
    public async Task SetScopeAsync_SetsResourceGroupFromScope()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        bicep.Scope = new("test-rg");

        var scope = new JsonObject();

        await BicepUtilities.SetScopeAsync(scope, bicep);

        Assert.Single(scope);
        Assert.Equal("test-rg", scope["resourceGroup"]?.ToString());
    }

    [Fact]
    public async Task SetScopeAsync_SetsResourceGroupAndSubscriptionFromScope()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        bicep.Scope = new("test-rg", "12345678-1234-1234-1234-123456789012");

        var scope = new JsonObject();

        await BicepUtilities.SetScopeAsync(scope, bicep);

        Assert.Equal(2, scope.Count);
        Assert.Equal("test-rg", scope["resourceGroup"]?.ToString());
        Assert.Equal("12345678-1234-1234-1234-123456789012", scope["subscription"]?.ToString());
    }

    [Fact]
    public async Task SetScopeAsync_SetsSubscriptionFromScope()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        bicep.Scope = AzureBicepResourceScope.ForSubscription("12345678-1234-1234-1234-123456789012");

        var scope = new JsonObject();

        await BicepUtilities.SetScopeAsync(scope, bicep);

        Assert.Single(scope);
        Assert.Equal("12345678-1234-1234-1234-123456789012", scope["subscription"]?.ToString());
    }

    [Fact]
    public async Task SetScopeAsync_RemovesStaleScopeValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        bicep.Scope = AzureBicepResourceScope.ForSubscription("12345678-1234-1234-1234-123456789012");

        var scope = new JsonObject
        {
            ["resourceGroup"] = "old-rg",
            ["tenant"] = "current"
        };

        await BicepUtilities.SetScopeAsync(scope, bicep);

        Assert.Single(scope);
        Assert.Equal("12345678-1234-1234-1234-123456789012", scope["subscription"]?.ToString());
    }

    [Fact]
    public async Task SetScopeAsync_SetsTenantFromScope()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        bicep.Scope = AzureBicepResourceScope.ForTenant();

        var scope = new JsonObject();

        await BicepUtilities.SetScopeAsync(scope, bicep);

        Assert.Single(scope);
        Assert.Equal("current", scope["tenant"]?.ToString());
    }

    [Fact]
    public async Task SetScopeAsync_SetsNullWhenNoScope()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;

        var scope = new JsonObject();

        await BicepUtilities.SetScopeAsync(scope, bicep);

        Assert.Single(scope);
        Assert.Null(scope["resourceGroup"]?.AsValue().GetValue<object>());
    }

    [Fact]
    public async Task GetCurrentChecksumAsync_ReturnsNullForMissingParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        var config = new ConfigurationBuilder().Build();

        var result = await BicepUtilities.GetCurrentChecksumAsync(bicep, config);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentChecksumAsync_ReturnsNullForInvalidJson()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;

        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters"] = "invalid-json"
        });
        var config = configurationBuilder.Build();

        var result = await BicepUtilities.GetCurrentChecksumAsync(bicep, config);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentChecksumAsync_ReturnsValidChecksumForValidParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        bicep.Parameters["param1"] = "value1";

        var parameters = new JsonObject
        {
            ["param1"] = new JsonObject { ["value"] = "value1" }
        };

        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters"] = parameters.ToJsonString()
        });
        var config = configurationBuilder.Build();

        var result = await BicepUtilities.GetCurrentChecksumAsync(bicep, config);

        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task GetCurrentChecksumAsync_UsesCurrentScopeWhenSavedScopeIsMissing()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string")
            .WithParameter("key", "value")
            .Resource;

        var legacyParameters = new JsonObject();
        await BicepUtilities.SetParametersAsync(legacyParameters, bicep);
        var legacyChecksum = BicepUtilities.GetChecksum(bicep, legacyParameters, scope: null);

        bicep.Scope = AzureBicepResourceScope.ForSubscription("12345678-1234-1234-1234-123456789012");

        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters"] = legacyParameters.ToJsonString()
        });
        var config = configurationBuilder.Build();

        var result = await BicepUtilities.GetCurrentChecksumAsync(bicep, config);

        var currentParameters = new JsonObject();
        var currentScope = new JsonObject();
        await BicepUtilities.SetParametersAsync(currentParameters, bicep, skipKnownValues: true);
        await BicepUtilities.SetScopeAsync(currentScope, bicep);
        var expected = BicepUtilities.GetChecksum(bicep, currentParameters, currentScope);

        Assert.Equal(expected, result);
        Assert.NotEqual(legacyChecksum, result);
    }

    /// <summary>
    /// Ensures that known parameters are not overwritten when calculating the checksum.
    /// This is important because if these known parameters are overwritten, it means the "roles"
    /// resources will be redeployed every time the app is run.
    /// </summary>
    [Fact]
    public async Task GetCurrentChecksumAsync_DoesNotOverwriteKnownParameters()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicep = builder.AddBicepTemplateString("test", "param name string").Resource;
        bicep.Parameters[AzureBicepResource.KnownParameters.PrincipalType] = null;
        bicep.Parameters[AzureBicepResource.KnownParameters.PrincipalId] = null;

        var parameters = new JsonObject
        {
            [AzureBicepResource.KnownParameters.PrincipalType] = new JsonObject { ["value"] = "User" },
            [AzureBicepResource.KnownParameters.PrincipalId] = new JsonObject { ["value"] = "1234" },
        };

        var configurationBuilder = new ConfigurationBuilder();
        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Parameters"] = parameters.ToJsonString()
        });
        var config = configurationBuilder.Build();

        // Act
        var result = await BicepUtilities.GetCurrentChecksumAsync(bicep, config);

        // Assert
        Assert.NotNull(result);

        // verify the checksum is the same as using the config parameters directly
        var expected = BicepUtilities.GetChecksum(bicep, parameters, scope: null);
        Assert.Equal(expected, result);
    }

    private sealed class ResourceWithConnectionString(string name, string connectionString) :
        Resource(name),
        IResourceWithConnectionString
    {
        public ReferenceExpression ConnectionStringExpression =>
           ReferenceExpression.Create($"{connectionString}");
    }
}
