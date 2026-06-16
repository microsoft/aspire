// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREFILESYSTEM001

using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Azure.Resources;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Azure.Security.KeyVault.Secrets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class AzureBicepProvisionerTests
{
    [Theory]
    [InlineData("1alpha")]
    [InlineData("-alpha")]
    [InlineData("")]
    [InlineData(" alpha")]
    [InlineData("alpha 123")]
    public void WithParameterDoesNotAllowParameterNamesWhichAreInvalidBicepIdentifiers(string bicepParameterName)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            using var builder = TestDistributedApplicationBuilder.Create();
            builder.AddAzureInfrastructure("infrastructure", _ => { })
                   .WithParameter(bicepParameterName);
        });
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("a1pha")]
    [InlineData("_alpha")]
    [InlineData("__alpha")]
    [InlineData("alpha1_")]
    [InlineData("Alpha1_A")]
    public void WithParameterAllowsParameterNamesWhichAreValidBicepIdentifiers(string bicepParameterName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureInfrastructure("infrastructure", _ => { })
                .WithParameter(bicepParameterName);
    }

    [Fact]
    public async Task NestedChildResourcesShouldGetUpdated()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.Create();

        var cosmos = builder.AddAzureCosmosDB("cosmosdb");
        var db = cosmos.AddCosmosDatabase("db");
        var entries = db.AddContainer("entries", "/id");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await foreach (var resourceEvent in rns.WatchAsync(cts.Token).WithCancellation(cts.Token))
        {
            if (resourceEvent.Resource == entries.Resource)
            {
                var parentProperty = resourceEvent.Snapshot.Properties.FirstOrDefault(x => x.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                Assert.Equal("db", parentProperty);
                return;
            }
        }

        Assert.Fail();
    }

    [Fact]
    public void BicepProvisioner_CanBeInstantiated()
    {
        // Test that BicepProvisioner can be instantiated with required dependencies

        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        var services = builder.Services.BuildServiceProvider();

        var bicepExecutor = new TestBicepCliExecutor();
        var secretClientProvider = new TestSecretClientProvider();
        var tokenCredentialProvider = new TestTokenCredentialProvider();

        // Act
        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            bicepExecutor,
            secretClientProvider,
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        // Assert
        Assert.NotNull(provisioner);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_InPublishMode_ThrowsForUnknownPrincipalParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage-roles", templateString: "output id string = 'ok'");
        resource.Parameters[AzureBicepResource.KnownParameters.PrincipalId] = null;
        resource.Parameters[AzureBicepResource.KnownParameters.PrincipalName] = null;
        resource.Parameters[AzureBicepResource.KnownParameters.PrincipalType] = null;

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            executionContext: new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Contains("Azure principal parameter was not supplied", exception.Message);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_WithSubscriptionScope_UsesSubscriptionDeploymentCollection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        using var services = builder.Services.BuildServiceProvider();

        var subscription = new TestSubscriptionResource([], subscriptionId: "00000000-0000-0000-0000-000000000042");
        var tenant = new TestTenantResource([]);
        var deploymentResourceGroup = new TestResourceGroupResource("deploy-rg");
        var resource = new AzureBicepResource("subscriptionScoped", templateString: """
            targetScope = 'subscription'
            output result string = 'ok'
            """);
        resource.Scope = AzureBicepResourceScope.ForSubscription(subscription.Id.Name);

        var provisioner = CreateProvisioner(services);
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: new TestArmClient(subscription, tenant),
            subscription: subscription,
            resourceGroup: deploymentResourceGroup,
            tenant: tenant,
            location: AzureLocation.WestUS2,
            executionContext: new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        Assert.True(subscription.Deployments.WasCreateOrUpdateCalled);
        Assert.False(deploymentResourceGroup.Deployments.WasCreateOrUpdateCalled);
        Assert.False(tenant.Deployments.WasCreateOrUpdateCalled);
        Assert.Equal(AzureLocation.WestUS2.Name, subscription.Deployments.Content!.Location?.Name);
        Assert.StartsWith("subscriptionScoped-", subscription.Deployments.DeploymentName);
        AssertResourceGroupPropertyIsNull(services, resource.Name);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_WithTenantScope_UsesTenantDeploymentCollection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        using var services = builder.Services.BuildServiceProvider();

        var subscription = new TestSubscriptionResource([]);
        var tenant = new TestTenantResource([]);
        var deploymentResourceGroup = new TestResourceGroupResource("deploy-rg");
        var resource = new AzureBicepResource("tenantScoped", templateString: """
            targetScope = 'tenant'
            output result string = 'ok'
            """);
        resource.Scope = AzureBicepResourceScope.ForTenant();

        var provisioner = CreateProvisioner(services);
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: new TestArmClient(subscription, tenant),
            subscription: subscription,
            resourceGroup: deploymentResourceGroup,
            tenant: tenant,
            location: AzureLocation.WestUS2,
            executionContext: new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        Assert.True(tenant.Deployments.WasCreateOrUpdateCalled);
        Assert.False(subscription.Deployments.WasCreateOrUpdateCalled);
        Assert.False(deploymentResourceGroup.Deployments.WasCreateOrUpdateCalled);
        Assert.Equal(AzureLocation.WestUS2.Name, tenant.Deployments.Content!.Location?.Name);
        Assert.StartsWith("tenantScoped-", tenant.Deployments.DeploymentName);
        AssertResourceGroupPropertyIsNull(services, resource.Name);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_WithResourceGroupAndSubscriptionScope_UsesScopedResourceGroupDeploymentCollection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        using var services = builder.Services.BuildServiceProvider();

        var scopedSubscriptionId = "00000000-0000-0000-0000-000000000043";
        var existingResourceGroup = new TestResourceGroupResource("existing-rg", [], scopedSubscriptionId);
        var scopedSubscription = new TestSubscriptionResource([], existingResourceGroup, scopedSubscriptionId);
        var deploymentResourceGroup = new TestResourceGroupResource("deploy-rg");
        var tenant = new TestTenantResource([]);
        var resource = new AzureBicepResource("existingbus", templateString: "output result string = 'ok'");
        resource.Scope = new AzureBicepResourceScope(existingResourceGroup.Name, scopedSubscriptionId);

        var provisioner = CreateProvisioner(services);
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: new TestArmClient(scopedSubscription, tenant),
            subscription: new TestSubscriptionResource([]),
            resourceGroup: deploymentResourceGroup,
            tenant: tenant,
            location: AzureLocation.WestUS2,
            executionContext: new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        Assert.Equal(existingResourceGroup.Name, scopedSubscription.ResourceGroups.LastRequestedResourceGroupName);
        Assert.True(existingResourceGroup.Deployments.WasCreateOrUpdateCalled);
        Assert.False(scopedSubscription.Deployments.WasCreateOrUpdateCalled);
        Assert.False(deploymentResourceGroup.Deployments.WasCreateOrUpdateCalled);
        Assert.Null(existingResourceGroup.Deployments.Content!.Location);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_WithDefaultScope_UsesResourceGroupDeploymentCollection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        using var services = builder.Services.BuildServiceProvider();

        var subscription = new TestSubscriptionResource([]);
        var tenant = new TestTenantResource([]);
        var resourceGroup = new TestResourceGroupResource("deploy-rg");
        var resource = new AzureBicepResource("defaultScoped", templateString: "output result string = 'ok'");

        var provisioner = CreateProvisioner(services);
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: new TestArmClient(subscription, tenant),
            subscription: subscription,
            resourceGroup: resourceGroup,
            tenant: tenant,
            location: AzureLocation.WestUS2,
            executionContext: new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        Assert.True(resourceGroup.Deployments.WasCreateOrUpdateCalled);
        Assert.False(subscription.Deployments.WasCreateOrUpdateCalled);
        Assert.False(tenant.Deployments.WasCreateOrUpdateCalled);
        Assert.Null(resourceGroup.Deployments.Content!.Location);
        Assert.StartsWith("defaultScoped-", resourceGroup.Deployments.DeploymentName);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_WithSubscriptionScopeInRunMode_UsesSubscriptionDeploymentCollection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        using var services = builder.Services.BuildServiceProvider();

        var subscription = new TestSubscriptionResource([], subscriptionId: "00000000-0000-0000-0000-000000000044");
        var tenant = new TestTenantResource([]);
        var deploymentResourceGroup = new TestResourceGroupResource("deploy-rg");
        var resource = new AzureBicepResource("subscriptionScoped", templateString: """
            targetScope = 'subscription'
            output result string = 'ok'
            """);
        resource.Scope = AzureBicepResourceScope.ForSubscription(subscription.Id.Name);

        var provisioner = CreateProvisioner(services, DistributedApplicationOperation.Run);
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: new TestArmClient(subscription, tenant),
            subscription: subscription,
            resourceGroup: deploymentResourceGroup,
            tenant: tenant,
            location: AzureLocation.WestUS2,
            executionContext: new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        Assert.True(subscription.Deployments.WasCreateOrUpdateCalled);
        Assert.False(deploymentResourceGroup.Deployments.WasCreateOrUpdateCalled);
        Assert.False(tenant.Deployments.WasCreateOrUpdateCalled);
        Assert.Equal(AzureLocation.WestUS2.Name, subscription.Deployments.Content!.Location?.Name);
        Assert.Equal("subscriptionScoped", subscription.Deployments.DeploymentName);
        AssertResourceGroupPropertyIsNull(services, resource.Name);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_InPublishMode_DoesNotQueryDeploymentOperationsAfterSuccessfulDeployment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage", templateString: "output name string = 'storage'");
        var armClient = new TestArmClient(
        [
            new AzureDeploymentOperationDetails(
                OperationId: "storage-create",
                DeploymentId: "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage",
                ProvisioningOperation: AzureDeploymentOperationDetails.CreateOperation,
                ProvisioningState: AzureDeploymentOperationDetails.SucceededState,
                Timestamp: null,
                Duration: null,
                StatusCode: "OK",
                ServiceRequestId: null,
                TargetResource: new AzureDeploymentOperationTarget(
                    "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage",
                    "Microsoft.Storage/storageAccounts",
                    "storage"),
                FailureDetails: null)
        ]);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: armClient,
            executionContext: new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        Assert.Equal(0, armClient.DeploymentOperationsCallCount);
        Assert.Equal(0, armClient.SupportedLocationsCallCount);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_InPublishMode_EnrichesDeploymentStartFailures()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("search", templateString: "output name string = 'search'");
        resource.Parameters[AzureBicepResource.KnownParameters.Location] = "australiacentral";
        var armClient = new TestArmClient(
            [],
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft.Search/searchServices"] = ["australiaeast", "westus3"]
            });

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: armClient,
            resourceGroup: new DeploymentCollectionResourceGroupResource(new LocationUnavailableArmDeploymentCollection()),
            executionContext: new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var exception = await Assert.ThrowsAsync<AzureProvisioningFailureException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Equal(AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode, exception.FailureDetails.ErrorCode);
        Assert.Equal("Microsoft.Search/searchServices", exception.FailureDetails.ResourceType);
        Assert.Equal("australiacentral", exception.FailureDetails.CurrentLocation);
        Assert.Equal(["australiaeast", "westus3"], exception.FailureDetails.SupportedLocations);
        Assert.DoesNotContain(exception.FailureDetails.RecommendedActions, static action => action.Code == "change-location");
        Assert.Contains(exception.FailureDetails.RecommendedActions, static action => action.Code == "set-location");
        Assert.Contains("Azure__Location", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, armClient.DeploymentOperationsCallCount);
        Assert.Equal(1, armClient.SupportedLocationsCallCount);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_InPublishMode_UsesDeploymentOperationDetailsWhenWaitingFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("search", templateString: "output name string = 'search'");
        var resourceGroup = new DeploymentCollectionResourceGroupResource(new WaitingThrowingArmDeploymentCollection());
        var deploymentId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/search-0";
        var failedSearchId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Search/searchServices/search";
        var failureDetails = new AzureProvisioningFailureDetails(
            Provider: "Microsoft.Search",
            ResourceType: "Microsoft.Search/searchServices",
            ResourceName: "search",
            TargetResourceId: failedSearchId,
            CurrentLocation: null,
            SupportedLocations: [],
            HttpStatus: 400,
            ErrorCode: AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode,
            ErrorMessage: "The provided location 'antarctica' is not available for resource type 'Microsoft.Search/searchServices'.",
            Operation: "deploy",
            RequestId: "request-id",
            CorrelationId: "correlation-id",
            RecommendedActions: AzureProvisioningFailureDetails.GetRecommendedActions(AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode));
        var armClient = new TestArmClient(
            [
                new AzureDeploymentOperationDetails(
                    OperationId: "search-create",
                    DeploymentId: deploymentId,
                    ProvisioningOperation: AzureDeploymentOperationDetails.CreateOperation,
                    ProvisioningState: AzureDeploymentOperationDetails.FailedState,
                    Timestamp: DateTimeOffset.Parse("2026-06-11T06:39:22Z"),
                    Duration: TimeSpan.FromSeconds(15),
                    StatusCode: "BadRequest",
                    ServiceRequestId: "service-request-id",
                    TargetResource: new AzureDeploymentOperationTarget(failedSearchId, "Microsoft.Search/searchServices", "search"),
                    FailureDetails: failureDetails)
            ],
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft.Search/searchServices"] = ["eastus", "westus3"]
            });

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: armClient,
            resourceGroup: resourceGroup,
            executionContext: new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var exception = await Assert.ThrowsAsync<AzureProvisioningFailureException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Equal(AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode, exception.FailureDetails.ErrorCode);
        Assert.Equal("Microsoft.Search/searchServices", exception.FailureDetails.ResourceType);
        Assert.Equal(failedSearchId, exception.FailureDetails.TargetResourceId);
        Assert.Equal("westus2", exception.FailureDetails.CurrentLocation);
        Assert.Equal(["eastus", "westus3"], exception.FailureDetails.SupportedLocations);
        Assert.Collection(
            exception.FailureDetails.RecommendedActions,
            action =>
            {
                Assert.Equal("set-location", action.Code);
                Assert.Contains("eastus", action.Message, StringComparison.Ordinal);
            },
            action => Assert.Equal("clear-deployment-cache", action.Code));
        Assert.Contains("Azure__Location", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, armClient.DeploymentOperationsCallCount);
        Assert.Equal(1, armClient.SupportedLocationsCallCount);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_InPublishMode_EnrichesDeploymentOperationFailuresInParallel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("search", templateString: "output name string = 'search'");
        var resourceGroup = new DeploymentCollectionResourceGroupResource(new WaitingThrowingArmDeploymentCollection());
        var deploymentId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/search-0";
        var failedSearchId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Search/searchServices/search";
        var failedCosmosId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.DocumentDB/databaseAccounts/cosmos";
        var allLookupsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeLookups = 0;
        var startedLookups = 0;
        var maxConcurrentLookups = 0;
        var armClient = new TestArmClient(
            [
                CreateLocationFailureOperation(deploymentId, "search-create", failedSearchId, "Microsoft.Search/searchServices", "search"),
                CreateLocationFailureOperation(deploymentId, "cosmos-create", failedCosmosId, "Microsoft.DocumentDB/databaseAccounts", "cosmos")
            ],
            supportedLocationsProvider: async (subscriptionId, resourceType, cancellationToken) =>
            {
                _ = subscriptionId;
                var active = Interlocked.Increment(ref activeLookups);
                try
                {
                    UpdateMaxConcurrentLookups(active);

                    if (Interlocked.Increment(ref startedLookups) == 2)
                    {
                        allLookupsStarted.TrySetResult();
                    }

                    await allLookupsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

                    return resourceType.Contains("Search", StringComparison.OrdinalIgnoreCase)
                        ? ["eastus"]
                        : ["westus3"];
                }
                finally
                {
                    Interlocked.Decrement(ref activeLookups);
                }
            });

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: armClient,
            resourceGroup: resourceGroup,
            executionContext: new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        await Assert.ThrowsAsync<AzureProvisioningFailureException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Equal(2, armClient.SupportedLocationsCallCount);
        Assert.Equal(2, Volatile.Read(ref maxConcurrentLookups));

        void UpdateMaxConcurrentLookups(int active)
        {
            while (true)
            {
                var observed = Volatile.Read(ref maxConcurrentLookups);
                if (active <= observed ||
                    Interlocked.CompareExchange(ref maxConcurrentLookups, active, observed) == observed)
                {
                    return;
                }
            }
        }

        static AzureDeploymentOperationDetails CreateLocationFailureOperation(
            string deploymentId,
            string operationId,
            string targetResourceId,
            string resourceType,
            string resourceName)
        {
            return new(
                OperationId: operationId,
                DeploymentId: deploymentId,
                ProvisioningOperation: AzureDeploymentOperationDetails.CreateOperation,
                ProvisioningState: AzureDeploymentOperationDetails.FailedState,
                Timestamp: DateTimeOffset.Parse("2026-06-11T06:39:22Z"),
                Duration: TimeSpan.FromSeconds(15),
                StatusCode: "BadRequest",
                ServiceRequestId: "service-request-id",
                TargetResource: new AzureDeploymentOperationTarget(targetResourceId, resourceType, resourceName),
                FailureDetails: new AzureProvisioningFailureDetails(
                    Provider: resourceType[..resourceType.IndexOf('/', StringComparison.Ordinal)],
                    ResourceType: resourceType,
                    ResourceName: resourceName,
                    TargetResourceId: targetResourceId,
                    CurrentLocation: null,
                    SupportedLocations: [],
                    HttpStatus: 400,
                    ErrorCode: AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode,
                    ErrorMessage: $"The provided location 'antarctica' is not available for resource type '{resourceType}'.",
                    Operation: "deploy",
                    RequestId: "request-id",
                    CorrelationId: "correlation-id",
                    RecommendedActions: AzureProvisioningFailureDetails.GetRecommendedActions(AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode)));
        }
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_UsesEffectiveResourceLocationInSnapshot()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage", templateString: "output name string = 'storage'");
        resource.Parameters[AzureBicepResource.KnownParameters.Location] = "westus3";

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(location: AzureLocation.WestUS2);

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        AssertHighlightedContextProperty(resourceEvent.Snapshot.Properties, "azure.subscription.id", "12345678-1234-1234-1234-123456789012", AzureProvisioningStrings.ContextPropertySubscriptionIdDisplayName);
        AssertHighlightedContextProperty(resourceEvent.Snapshot.Properties, "azure.resource.group", "test-rg", AzureProvisioningStrings.ContextPropertyResourceGroupDisplayName);
        AssertHighlightedContextProperty(resourceEvent.Snapshot.Properties, "azure.tenant.id", "87654321-4321-4321-4321-210987654321", AzureProvisioningStrings.ContextPropertyTenantIdDisplayName);
        AssertHighlightedContextProperty(resourceEvent.Snapshot.Properties, "azure.tenant.domain", "testdomain.onmicrosoft.com", AzureProvisioningStrings.ContextPropertyTenantDomainDisplayName);
        AssertHighlightedContextProperty(resourceEvent.Snapshot.Properties, "azure.location", "westus3", AzureProvisioningStrings.ContextPropertyLocationDisplayName);
        Assert.Equal("/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage", resourceEvent.Snapshot.Properties.Single(p => p.Name == CustomResourceKnownProperties.Source).Value?.ToString());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_PublishesPredictedDeploymentIdBeforeDeploymentStarts()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var resourceGroup = new ThrowingResourceGroupResource("test-rg");

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<RequestFailedException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        Assert.Equal("87654321-4321-4321-4321-210987654321", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.tenant.id").Value?.ToString());
        Assert.Equal("/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage2", resourceEvent.Snapshot.Properties.Single(p => p.Name == CustomResourceKnownProperties.Source).Value?.ToString());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_PublishesSubscriptionScopedPredictedDeploymentIdAndUrlWhileWaiting()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var subscription = new WaitingThrowingSubscriptionResource();
        var tenant = new TestTenantResource();
        var resource = new AzureBicepResource("subscriptionDeployment", templateString: "output name string = 'subscriptionDeployment'")
        {
            Scope = AzureBicepResourceScope.ForSubscription(subscription.Id.Name)
        };

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: new TestArmClient(subscription, tenant),
            subscription: subscription,
            tenant: tenant);

        await Assert.ThrowsAsync<RequestFailedException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        var expectedDeploymentId = "/subscriptions/12345678-1234-1234-1234-123456789012/providers/Microsoft.Resources/deployments/subscriptionDeployment";
        Assert.Equal("87654321-4321-4321-4321-210987654321", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.tenant.id").Value?.ToString());
        Assert.Equal(expectedDeploymentId, resourceEvent.Snapshot.Properties.Single(p => p.Name == CustomResourceKnownProperties.Source).Value?.ToString());
        Assert.Equal(BicepProvisioner.GetDeploymentUrl(new ResourceIdentifier(expectedDeploymentId)), resourceEvent.Snapshot.Urls.Single(u => u.Name == "deployment").Url);
    }

    [Fact]
    public async Task ConfigureResourceAsync_DoesNotReuseOverrideOnlyDeploymentState()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var reused = await provisioner.ConfigureResourceAsync(resource, CancellationToken.None);

        Assert.False(reused);
        Assert.Empty(resource.Outputs);
    }

    [Fact]
    public async Task ConfigureResourceAsync_DoesNotReuseInProgressDeploymentState()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var parameters = new JsonObject();
        var checksum = BicepUtilities.GetChecksum(resource, parameters, scope: null);

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data["Id"] = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage2";
        section.Data["Parameters"] = parameters.ToJsonString();
        section.Data["Outputs"] = """{"name":{"value":"storage2"}}""";
        section.Data["CheckSum"] = checksum;
        section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateRunning;
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var reused = await provisioner.ConfigureResourceAsync(resource, CancellationToken.None);

        Assert.False(reused);
        Assert.Empty(resource.Outputs);
    }

    [Fact]
    public async Task ConfigureResourceAsync_PublishesAzureIdentityPropertiesFromDeploymentState()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var parameters = new JsonObject();
        var checksum = BicepUtilities.GetChecksum(resource, parameters, scope: null);

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure", CancellationToken.None);
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["ResourceGroup"] = "test-rg";
        azureSection.Data["TenantId"] = "87654321-4321-4321-4321-210987654321";
        azureSection.Data["Tenant"] = "microsoft.onmicrosoft.com";
        azureSection.Data["Location"] = "westus2";
        await deploymentStateManager.SaveSectionAsync(azureSection, CancellationToken.None);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data["Id"] = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage2";
        section.Data["Parameters"] = parameters.ToJsonString();
        section.Data["Outputs"] = """{"name":{"value":"storage2"}}""";
        section.Data["CheckSum"] = checksum;
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var reused = await provisioner.ConfigureResourceAsync(resource, CancellationToken.None);

        Assert.True(reused);

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        Assert.Equal("12345678-1234-1234-1234-123456789012", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.subscription.id").Value?.ToString());
        Assert.Equal("test-rg", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.resource.group").Value?.ToString());
        Assert.Equal("87654321-4321-4321-4321-210987654321", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.tenant.id").Value?.ToString());
        Assert.Equal("microsoft.onmicrosoft.com", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.tenant.domain").Value?.ToString());
        Assert.Equal("westus2", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.location").Value?.ToString());
        Assert.Equal("/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage2", resourceEvent.Snapshot.Properties.Single(p => p.Name == CustomResourceKnownProperties.Source).Value?.ToString());
    }

    [Fact]
    public async Task ConfigureResourceAsync_PublishesResourceGroupFromCachedDeploymentId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var parameters = new JsonObject();
        var checksum = BicepUtilities.GetChecksum(resource, parameters, scope: null);

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure", CancellationToken.None);
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["ResourceGroup"] = "environment-rg";
        azureSection.Data["TenantId"] = "87654321-4321-4321-4321-210987654321";
        azureSection.Data["Tenant"] = "microsoft.onmicrosoft.com";
        azureSection.Data["Location"] = "westus2";
        await deploymentStateManager.SaveSectionAsync(azureSection, CancellationToken.None);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data["Id"] = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/scoped-rg/providers/Microsoft.Resources/deployments/storage2";
        section.Data["Parameters"] = parameters.ToJsonString();
        section.Data["Outputs"] = """{"name":{"value":"storage2"}}""";
        section.Data["CheckSum"] = checksum;
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var reused = await provisioner.ConfigureResourceAsync(resource, CancellationToken.None);

        Assert.True(reused);

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        Assert.Equal("scoped-rg", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.resource.group").Value?.ToString());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_PreservesLocationOverrideInDeploymentState()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        resource.Parameters[AzureBicepResource.KnownParameters.Location] = "westus3";

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(location: AzureLocation.WestUS2);

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal("westus3", section.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
        Assert.False(section.Data.ContainsKey(BicepProvisioner.DeploymentStateProvisioningStateKey));
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_ClearsStaleLocationOverrideWhenEffectiveLocationChanges()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(location: AzureLocation.WestUS2);

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.False(section.Data.ContainsKey(AzureProvisioningController.LocationOverrideKey));
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_SavesInProgressDeploymentStateBeforeWaiting()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var resourceGroup = new DeploymentCollectionResourceGroupResource(new WaitingThrowingArmDeploymentCollection());

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<RequestFailedException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal("/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage2", section.Data["Id"]?.GetValue<string>());
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateRunning, section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());
        Assert.True(section.Data.ContainsKey("Parameters"));
        Assert.True(section.Data.ContainsKey("CheckSum"));
        Assert.False(section.Data.ContainsKey("Outputs"));
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_PublishesFailedDeploymentOperationDetailsWhenWaitingFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("search", templateString: "output name string = 'search'");
        var resourceGroup = new DeploymentCollectionResourceGroupResource(new WaitingThrowingArmDeploymentCollection());
        var deploymentId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/search";
        var failedSearchId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Search/searchServices/search";
        var failureDetails = new AzureProvisioningFailureDetails(
            Provider: "Microsoft.Search",
            ResourceType: "Microsoft.Search/searchServices",
            ResourceName: "search",
            TargetResourceId: failedSearchId,
            CurrentLocation: null,
            SupportedLocations: [],
            HttpStatus: 400,
            ErrorCode: AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode,
            ErrorMessage: "The provided location 'antarctica' is not available for resource type 'Microsoft.Search/searchServices'.",
            Operation: "deploy",
            RequestId: "request-id",
            CorrelationId: "correlation-id",
            RecommendedActions: AzureProvisioningFailureDetails.GetRecommendedActions(AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode));
        var operations = new[]
        {
            new AzureDeploymentOperationDetails(
                OperationId: "search-create",
                DeploymentId: deploymentId,
                ProvisioningOperation: AzureDeploymentOperationDetails.CreateOperation,
                ProvisioningState: AzureDeploymentOperationDetails.FailedState,
                Timestamp: DateTimeOffset.Parse("2026-06-11T06:39:22Z"),
                Duration: TimeSpan.FromSeconds(15),
                StatusCode: "BadRequest",
                ServiceRequestId: "service-request-id",
                TargetResource: new AzureDeploymentOperationTarget(failedSearchId, "Microsoft.Search/searchServices", "search"),
                FailureDetails: failureDetails),
            new AzureDeploymentOperationDetails(
                OperationId: "alpha-search-create",
                DeploymentId: deploymentId,
                ProvisioningOperation: AzureDeploymentOperationDetails.CreateOperation,
                ProvisioningState: AzureDeploymentOperationDetails.FailedState,
                Timestamp: DateTimeOffset.Parse("2026-06-11T06:39:21Z"),
                Duration: TimeSpan.FromSeconds(14),
                StatusCode: "BadRequest",
                ServiceRequestId: "alpha-service-request-id",
                TargetResource: new AzureDeploymentOperationTarget(
                    "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Search/searchServices/alpha-search",
                    "Microsoft.Search/searchServices",
                    "alpha-search"),
                FailureDetails: null),
            new AzureDeploymentOperationDetails(
                OperationId: "nested-deployment",
                DeploymentId: deploymentId,
                ProvisioningOperation: AzureDeploymentOperationDetails.CreateOperation,
                ProvisioningState: AzureDeploymentOperationDetails.SucceededState,
                Timestamp: DateTimeOffset.Parse("2026-06-11T06:39:20Z"),
                Duration: TimeSpan.FromSeconds(5),
                StatusCode: "OK",
                ServiceRequestId: "nested-request-id",
                TargetResource: new AzureDeploymentOperationTarget($"{deploymentId}/nested", AzureDeploymentOperationDetails.DeploymentResourceType, "nested"),
                FailureDetails: null)
        };

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: new TestArmClient(
                operations,
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Microsoft.Search/searchServices"] = ["eastus", "westus3"]
                }),
            resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<AzureProvisioningFailureException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        Assert.Equal("Azure deployment failed", resourceEvent.Snapshot.State?.Text);
        Assert.Collection(
            resourceEvent.Snapshot.Properties,
            property => AssertHighlightedResourceProperty(property, "azure.subscription.id", "12345678-1234-1234-1234-123456789012", AzureProvisioningStrings.ContextPropertySubscriptionIdDisplayName),
            property => AssertHighlightedResourceProperty(property, "azure.resource.group", "test-rg", AzureProvisioningStrings.ContextPropertyResourceGroupDisplayName),
            property => AssertHighlightedResourceProperty(property, "azure.tenant.id", "87654321-4321-4321-4321-210987654321", AzureProvisioningStrings.ContextPropertyTenantIdDisplayName),
            property => AssertHighlightedResourceProperty(property, "azure.tenant.domain", "testdomain.onmicrosoft.com", AzureProvisioningStrings.ContextPropertyTenantDomainDisplayName),
            property => AssertHighlightedResourceProperty(property, "azure.location", "westus2", AzureProvisioningStrings.ContextPropertyLocationDisplayName),
            property => AssertResourceProperty(property, CustomResourceKnownProperties.Source, deploymentId),
            property => AssertResourceProperty(property, "azure.deployment.operations.total", 3),
            property => AssertResourceProperty(property, "azure.deployment.operations.running", 0),
            property => AssertResourceProperty(property, "azure.deployment.operations.succeeded", 0),
            property => AssertResourceProperty(property, "azure.deployment.operations.failed", 2),
            property => AssertResourceProperty(property, "azure.deployment.operations.canceled", 0),
            property =>
            {
                var failedResources = AssertStringArrayProperty(property, "azure.deployment.operations.failed.resources");
                Assert.Equal(["alpha-search (Microsoft.Search/searchServices)", "search (Microsoft.Search/searchServices)"], failedResources);
            },
            property => AssertResourceProperty(property, "azure.provisioning.error.provider", "Microsoft.Search"),
            property => AssertResourceProperty(property, "azure.provisioning.error.message", "The provided location 'antarctica' is not available for resource type 'Microsoft.Search/searchServices'."),
            property => AssertResourceProperty(property, "azure.provisioning.error.operation", "deploy"),
            property => AssertResourceProperty(property, "azure.provisioning.error.resource.type", "Microsoft.Search/searchServices"),
            property => AssertResourceProperty(property, "azure.provisioning.error.resource.name", "search"),
            property => AssertResourceProperty(property, "azure.provisioning.error.target.resource.id", failedSearchId),
            property => AssertResourceProperty(property, "azure.provisioning.error.current.location", "westus2"),
            property =>
            {
                var supportedLocations = AssertStringArrayProperty(property, "azure.provisioning.error.supported.locations");
                Assert.Equal(["eastus", "westus3"], supportedLocations);
            },
            property => AssertResourceProperty(property, "azure.provisioning.error.code", AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode),
            property => AssertResourceProperty(property, "azure.provisioning.error.http.status", 400),
            property => AssertResourceProperty(property, "azure.provisioning.error.request.id", "request-id"),
            property => AssertResourceProperty(property, "azure.provisioning.error.correlation.id", "correlation-id"),
            property =>
            {
                var recommendedActions = AssertStringArrayProperty(property, "azure.provisioning.error.recommendedActions");
                Assert.Collection(
                    recommendedActions,
                    action => Assert.Contains("change-location --location eastus", action, StringComparison.Ordinal),
                    action => Assert.Contains("Supported regions include: eastus, westus3", action, StringComparison.Ordinal));
            });

        Assert.All(
            resourceEvent.Snapshot.Properties.Where(property => property.Name.StartsWith("azure.deployment.operations.", StringComparison.Ordinal)),
            property => Assert.False(property.IsHighlighted));

        static void AssertResourceProperty(ResourcePropertySnapshot property, string name, object value)
        {
            Assert.Equal(name, property.Name);
            Assert.Equal(value, property.Value);
        }

        static string[] AssertStringArrayProperty(ResourcePropertySnapshot property, string name)
        {
            Assert.Equal(name, property.Name);
            return Assert.IsType<string[]>(property.Value);
        }

        var resourceLogs = await ReadInitialResourceLogsAsync(services.GetRequiredService<ResourceLoggerService>(), resource);
        Assert.Contains(resourceLogs, log =>
            log.IsErrorMessage &&
            log.Content.Contains("Azure provisioning failed:", StringComparison.Ordinal) &&
            log.Content.Contains(AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_ClearsStaleDeploymentOperationDetailsWhenDeploymentStartFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("search", templateString: "output name string = 'search'");
        resource.Parameters[AzureBicepResource.KnownParameters.Location] = "australiacentral";
        var resourceGroup = new DeploymentCollectionResourceGroupResource(new LocationUnavailableArmDeploymentCollection());
        var notifications = services.GetRequiredService<ResourceNotificationService>();

        await notifications.PublishUpdateAsync(resource, state => state with
        {
            Properties =
            [
                new("azure.deployment.operations.total", 2),
                new("azure.deployment.operations.succeeded", 1),
                new("azure.deployment.operations.succeeded.resources", new[] { "old-search (Microsoft.Search/searchServices)" })
            ]
        });

        var provisioner = new BicepProvisioner(
            notifications,
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            armClient: new TestArmClient(
                [],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Microsoft.Search/searchServices"] = ["australiaeast", "westus3"]
                }),
            resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<AzureProvisioningFailureException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        Assert.Equal("Azure deployment failed", resourceEvent.Snapshot.State?.Text);
        Assert.DoesNotContain(resourceEvent.Snapshot.Properties, p => p.Name.StartsWith("azure.deployment.operations.", StringComparison.Ordinal));
        Assert.Equal(AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode, resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.provisioning.error.code").Value);
        Assert.Equal("australiacentral", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.provisioning.error.current.location").Value);
        var supportedLocations = Assert.IsType<string[]>(resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.provisioning.error.supported.locations").Value);
        Assert.Equal(["australiaeast", "westus3"], supportedLocations);

        var resourceLogs = await ReadInitialResourceLogsAsync(services.GetRequiredService<ResourceLoggerService>(), resource);
        Assert.Contains(resourceLogs, log =>
            log.IsErrorMessage &&
            log.Content.Contains("Azure provisioning failed:", StringComparison.Ordinal) &&
            log.Content.Contains(AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_CancelsStartedDeploymentWhenWaitIsCanceled()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var deploymentCollection = new CancelingArmDeploymentCollection();
        var resourceGroup = new DeploymentCollectionResourceGroupResource(deploymentCollection);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<OperationCanceledException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Equal(1, deploymentCollection.CancelCallCount);
        Assert.Equal("storage2", deploymentCollection.CanceledDeploymentName);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateCanceled, section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_CancelsPendingDeploymentWhenStartIsCanceled()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var deploymentCollection = new PendingCancelArmDeploymentCollection();
        var resourceGroup = new DeploymentCollectionResourceGroupResource(deploymentCollection);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<OperationCanceledException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Equal(1, deploymentCollection.CancelCallCount);
        Assert.Equal("storage2", deploymentCollection.CanceledDeploymentName);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateCanceled, section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_PersistsCanceledStateWhenCancelFindsAlreadyInactiveDeployment()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var deploymentCollection = new AlreadyCanceledArmDeploymentCollection();
        var resourceGroup = new DeploymentCollectionResourceGroupResource(deploymentCollection);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<OperationCanceledException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Equal(1, deploymentCollection.CancelCallCount);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateCanceled, section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_SavesTerminalDeploymentStateWhenDeploymentFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var resourceGroup = new DeploymentCollectionResourceGroupResource(new FailingArmDeploymentCollection());

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Contains(ResourcesProvisioningState.Failed.ToString(), exception.Message);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal(ResourcesProvisioningState.Failed.ToString(), section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());
    }

    [Fact]
    public async Task BicepCliExecutor_CompilesBicepToArm()
    {
        // Test the mock bicep executor behavior

        // Arrange
        var bicepExecutor = new TestBicepCliExecutor();

        // Act
        var result = await bicepExecutor.CompileBicepToArmAsync("test.bicep", CancellationToken.None);

        // Assert
        Assert.True(bicepExecutor.CompileBicepToArmAsyncCalled);
        Assert.Equal("test.bicep", bicepExecutor.LastCompiledPath);
        Assert.NotNull(result);
        Assert.Contains("$schema", result);
    }

    [Fact]
    public void SecretClientProvider_CreatesSecretClient()
    {
        // Test the mock secret client provider behavior

        // Arrange
        var secretClientProvider = new TestSecretClientProvider();
        var vaultUri = new Uri("https://test.vault.azure.net/");

        // Act
        var client = secretClientProvider.GetSecretClient(vaultUri);

        // Assert
        Assert.True(secretClientProvider.GetSecretClientCalled);
        // Client will be null in our mock, but the call was tracked
        Assert.Null(client);
    }

    [Fact]
    public void TestTokenCredential_ProvidesAccessToken()
    {
        // Test the mock token credential behavior

        // Arrange
        var tokenProvider = new TestTokenCredentialProvider();
        var credential = tokenProvider.TokenCredential;
        var requestContext = new TokenRequestContext(["https://management.azure.com/.default"]);

        // Act
        var token = credential.GetToken(requestContext, CancellationToken.None);

        // Assert
        Assert.Equal("mock-token", token.Token);
        Assert.True(token.ExpiresOn > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task TestTokenCredential_ProvidesAccessTokenAsync()
    {
        // Test the mock token credential async behavior

        // Arrange
        var provider = new TestTokenCredentialProvider();
        var credential = provider.TokenCredential;
        var requestContext = new TokenRequestContext(["https://management.azure.com/.default"]);

        // Act
        var token = await credential.GetTokenAsync(requestContext, CancellationToken.None);

        // Assert
        Assert.Equal("mock-token", token.Token);
        Assert.True(token.ExpiresOn > DateTimeOffset.UtcNow);
    }

    private static void AssertResourceGroupPropertyIsNull(IServiceProvider services, string resourceName)
    {
        var notificationService = services.GetRequiredService<ResourceNotificationService>();
        var resourceEvent = notificationService.TryGetCurrentState(resourceName, out var currentState)
            ? currentState
            : throw new InvalidOperationException($"Expected current state for resource '{resourceName}'.");
        var property = Assert.Single(resourceEvent.Snapshot.Properties, p => p.Name == "azure.resource.group");

        Assert.Null(property.Value);
    }

    private static void AssertHighlightedContextProperty(IEnumerable<ResourcePropertySnapshot> properties, string name, object value, string displayName)
    {
        var property = Assert.Single(properties, p => p.Name == name);
        AssertHighlightedResourceProperty(property, name, value, displayName);
    }

    private static void AssertHighlightedResourceProperty(ResourcePropertySnapshot property, string name, object value, string displayName)
    {
        Assert.Equal(name, property.Name);
        Assert.Equal(value, property.Value);
        Assert.Equal(displayName, property.DisplayName);
        Assert.True(property.IsHighlighted);
    }

    private static async Task<IReadOnlyList<(string Content, bool IsErrorMessage)>> ReadInitialResourceLogsAsync(ResourceLoggerService loggerService, IResource resource)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var logEnumerator = loggerService.WatchAsync(resource).GetAsyncEnumerator(cts.Token);

        if (!await logEnumerator.MoveNextAsync())
        {
            return [];
        }

        return [.. logEnumerator.Current.Select(static log => (log.Content, log.IsErrorMessage))];
    }

    private static BicepProvisioner CreateProvisioner(IServiceProvider services, DistributedApplicationOperation operation = DistributedApplicationOperation.Publish)
    {
        return new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(operation),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);
    }

    private sealed class TestTokenCredentialProvider : ITokenCredentialProvider
    {
        public TokenCredential TokenCredential => new MockTokenCredential();

        private sealed class MockTokenCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new("mock-token", DateTimeOffset.UtcNow.AddHours(1));

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                ValueTask.FromResult(new AccessToken("mock-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class TestBicepCliExecutor : IBicepCompiler
    {
        public bool CompileBicepToArmAsyncCalled { get; private set; }
        public string? LastCompiledPath { get; private set; }
        public string CompilationResult { get; set; } = """{"$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#"}""";

        public Task<string> CompileBicepToArmAsync(string bicepFilePath, CancellationToken cancellationToken = default)
        {
            CompileBicepToArmAsyncCalled = true;
            LastCompiledPath = bicepFilePath;
            return Task.FromResult(CompilationResult);
        }
    }

    private sealed class TestSecretClientProvider : ISecretClientProvider
    {
        public bool GetSecretClientCalled { get; private set; }

        public SecretClient GetSecretClient(Uri vaultUri)
        {
            GetSecretClientCalled = true;
            // Return null - this will fail in actual secret operations but allows testing the call
            return null!;
        }
    }

    private sealed class MockDeploymentStateManager : IDeploymentStateManager
    {
        public string? StateFilePath => null;

        public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeploymentStateSection(sectionName, [], 0));
        }

        public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearAllStateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DeploymentCollectionResourceGroupResource(IArmDeploymentCollection armDeploymentCollection) : IResourceGroupResource
    {
        public ResourceIdentifier Id { get; } = new("/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg");
        public string Name => "test-rg";

        public IArmDeploymentCollection GetArmDeployments() => armDeploymentCollection;

        public Task<ArmOperation> DeleteAsync(WaitUntil waitUntil, CancellationToken cancellationToken = default) =>
            Task.FromResult<ArmOperation>(new TestDeleteArmOperation());

        public async IAsyncEnumerable<(string Name, string ResourceType)> GetResourcesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ThrowingResourceGroupResource(string name) : IResourceGroupResource
    {
        private int _deleteCallCount;

        public int DeleteCallCount => _deleteCallCount;

        public ResourceIdentifier Id => new($"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/{name}");
        public string Name => name;

        public IArmDeploymentCollection GetArmDeployments() => new ThrowingArmDeploymentCollection();

        public Task<ArmOperation> DeleteAsync(WaitUntil waitUntil, CancellationToken cancellationToken = default)
        {
            _deleteCallCount++;
            return Task.FromResult<ArmOperation>(new TestDeleteArmOperation());
        }

        public async IAsyncEnumerable<(string Name, string ResourceType)> GetResourcesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class WaitingThrowingSubscriptionResource : ISubscriptionResource
    {
        public ResourceIdentifier Id { get; } = new("/subscriptions/12345678-1234-1234-1234-123456789012");

        public string? DisplayName => "Test Subscription";

        public Guid? TenantId => Guid.Parse("87654321-4321-4321-4321-210987654321");

        public IResourceGroupCollection GetResourceGroups() => new TestResourceGroupCollection();

        public IArmDeploymentCollection GetArmDeployments() => new WaitingThrowingArmDeploymentCollection();
    }

    private sealed class WaitingThrowingArmDeploymentCollection : IArmDeploymentCollection
    {
        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ArmOperation<ArmDeploymentResource>>(new WaitingThrowingArmDeploymentOperation());

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class WaitingThrowingArmDeploymentOperation : ArmOperation<ArmDeploymentResource>
    {
        public override string Id { get; } = Guid.NewGuid().ToString();

        public override ArmDeploymentResource Value => throw new InvalidOperationException("The deployment did not complete successfully.");

        public override bool HasCompleted => false;

        public override bool HasValue => false;

        public override Response GetRawResponse() => new MockResponse(200);

        public override Response UpdateStatus(CancellationToken cancellationToken = default) => new MockResponse(200);

        public override ValueTask<Response> UpdateStatusAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<Response>(new MockResponse(200));

        public override ValueTask<Response<ArmDeploymentResource>> WaitForCompletionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<Response<ArmDeploymentResource>>(new RequestFailedException(409, "Deployment wait failed."));

        public override ValueTask<Response<ArmDeploymentResource>> WaitForCompletionAsync(TimeSpan pollingInterval, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<Response<ArmDeploymentResource>>(new RequestFailedException(409, "Deployment wait failed."));

        public override Response<ArmDeploymentResource> WaitForCompletion(CancellationToken cancellationToken = default) =>
            throw new RequestFailedException(409, "Deployment wait failed.");

        public override Response<ArmDeploymentResource> WaitForCompletion(TimeSpan pollingInterval, CancellationToken cancellationToken = default) =>
            throw new RequestFailedException(409, "Deployment wait failed.");
    }

    private sealed class ThrowingArmDeploymentCollection : IArmDeploymentCollection
    {
        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            throw new RequestFailedException(409, "Deployment creation failed.");

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class LocationUnavailableArmDeploymentCollection : IArmDeploymentCollection
    {
        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ArmOperation<ArmDeploymentResource>>(new RequestFailedException(
                400,
                "The provided location 'australiacentral' is not available for resource type 'Microsoft.Search/searchServices'.",
                AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode,
                innerException: null));

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CancelingArmDeploymentCollection : IArmDeploymentCollection
    {
        public int CancelCallCount { get; private set; }
        public string? CanceledDeploymentName { get; private set; }

        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ArmOperation<ArmDeploymentResource>>(new CancelingArmDeploymentOperation());

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default)
        {
            CancelCallCount++;
            CanceledDeploymentName = deploymentName;
            return Task.CompletedTask;
        }
    }

    private sealed class CancelingArmDeploymentOperation : ArmOperation<ArmDeploymentResource>
    {
        public override string Id { get; } = Guid.NewGuid().ToString();

        public override ArmDeploymentResource Value => throw new InvalidOperationException("The deployment did not complete successfully.");

        public override bool HasCompleted => false;

        public override bool HasValue => false;

        public override Response GetRawResponse() => new MockResponse(200);

        public override Response UpdateStatus(CancellationToken cancellationToken = default) => new MockResponse(200);

        public override ValueTask<Response> UpdateStatusAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<Response>(new MockResponse(200));

        public override ValueTask<Response<ArmDeploymentResource>> WaitForCompletionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<Response<ArmDeploymentResource>>(new OperationCanceledException(cancellationToken));

        public override ValueTask<Response<ArmDeploymentResource>> WaitForCompletionAsync(TimeSpan pollingInterval, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<Response<ArmDeploymentResource>>(new OperationCanceledException(cancellationToken));

        public override Response<ArmDeploymentResource> WaitForCompletion(CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);

        public override Response<ArmDeploymentResource> WaitForCompletion(TimeSpan pollingInterval, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);
    }

    private sealed class PendingCancelArmDeploymentCollection : IArmDeploymentCollection
    {
        public int CancelCallCount { get; private set; }
        public string? CanceledDeploymentName { get; private set; }

        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ArmOperation<ArmDeploymentResource>>(new OperationCanceledException(cancellationToken));

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default)
        {
            CancelCallCount++;
            CanceledDeploymentName = deploymentName;
            return Task.CompletedTask;
        }
    }

    private sealed class AlreadyCanceledArmDeploymentCollection : IArmDeploymentCollection
    {
        public int CancelCallCount { get; private set; }

        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ArmOperation<ArmDeploymentResource>>(new CancelingArmDeploymentOperation());

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default)
        {
            CancelCallCount++;
            throw new RequestFailedException(409, "The deployment is already canceled.");
        }
    }

    private sealed class FailingArmDeploymentCollection : IArmDeploymentCollection
    {
        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ArmOperation<ArmDeploymentResource>>(new TestArmOperation<ArmDeploymentResource>(
                new TestArmDeploymentResource(deploymentName, [], ResourcesProvisioningState.Failed)));

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
