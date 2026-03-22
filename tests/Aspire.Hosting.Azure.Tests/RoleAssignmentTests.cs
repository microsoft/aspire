// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Azure.Provisioning.AppConfiguration;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.EventHubs;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.Search;
using Azure.Provisioning.ServiceBus;
using Azure.Provisioning.SignalR;
using Azure.Provisioning.WebPubSub;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class RoleAssignmentTests()
{
    [Fact]
    public Task ServiceBusSupport()
    {
        return RoleAssignmentTest("sb",
            builder =>
            {
                var sb = builder.AddAzureServiceBus("sb");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithRoleAssignments(sb, ServiceBusBuiltInRole.AzureServiceBusDataReceiver, ServiceBusBuiltInRole.AzureServiceBusDataSender);
            });
    }

    [Fact]
    public Task AppConfigurationSupport()
    {
        return RoleAssignmentTest("config",
            builder =>
            {
                var config = builder.AddAzureAppConfiguration("config");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithRoleAssignments(config, AppConfigurationBuiltInRole.AppConfigurationDataReader);
            });
    }

    [Fact]
    public Task OpenAISupport()
    {
        return RoleAssignmentTest("openai",
            builder =>
            {
                var openai = builder.AddAzureOpenAI("openai");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithRoleAssignments(openai, CognitiveServicesBuiltInRole.CognitiveServicesOpenAIUser, CognitiveServicesBuiltInRole.CognitiveServicesFaceRecognizer);
            });
    }

    [Fact]
    public Task AzureFoundrySupport()
    {
        return RoleAssignmentTest("ai",
            builder =>
            {
                var openai = builder.AddFoundry("ai");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithRoleAssignments(openai, CognitiveServicesBuiltInRole.CognitiveServicesFaceRecognizer);
            });
    }

    [Fact]
    public Task EventHubsSupport()
    {
        return RoleAssignmentTest("eventhubs",
            builder =>
            {
                var eventhubs = builder.AddAzureEventHubs("eventhubs");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithRoleAssignments(eventhubs, EventHubsBuiltInRole.AzureEventHubsDataReceiver);
            });
    }

    [Fact]
    public Task KeyVaultSupport()
    {
        return RoleAssignmentTest("keyvault",
            builder =>
            {
                var keyvault = builder.AddAzureKeyVault("keyvault");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithRoleAssignments(keyvault, KeyVaultBuiltInRole.KeyVaultSecretsUser);
            });
    }

    [Fact]
    public Task SearchSupport()
    {
        return RoleAssignmentTest("search",
            builder =>
            {
                var search = builder.AddAzureSearch("search");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithRoleAssignments(search, SearchBuiltInRole.SearchIndexDataReader);
            });
    }

    [Fact]
    public Task SignalRSupport()
    {
        return RoleAssignmentTest("signalr",
            builder =>
            {
                var signalr = builder.AddAzureSignalR("signalr");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithRoleAssignments(signalr, SignalRBuiltInRole.SignalRContributor);
            });
    }

    [Fact]
    public Task WebPubSubSupport()
    {
        return RoleAssignmentTest("webpubsub",
            builder =>
            {
                var webpubsub = builder.AddAzureWebPubSub("webpubsub");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithRoleAssignments(webpubsub, WebPubSubBuiltInRole.WebPubSubServiceReader);
            });
    }

    [Fact]
    public Task CosmosDBSupport()
    {
        return RoleAssignmentTest("cosmos",
            builder =>
            {
                var redis = builder.AddAzureCosmosDB("cosmos");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithReference(redis);
            });
    }

    [Fact]
    [Obsolete]
    public Task RedisSupport()
    {
        return RoleAssignmentTest("redis",
            builder =>
            {
                var redis = builder.AddAzureRedis("redis");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithReference(redis);
            });
    }

    [Fact]
    public Task RedisEnterpriseSupport()
    {
        return RoleAssignmentTest("redis",
            builder =>
            {
                var redis = builder.AddAzureManagedRedis("redis");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithReference(redis);
            });
    }

    [Fact]
    public Task PostgresSupport()
    {
        return RoleAssignmentTest("postgres",
            builder =>
            {
                var redis = builder.AddAzurePostgresFlexibleServer("postgres");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithReference(redis);
            });
    }

    [Fact]
    public Task SqlSupport()
    {
        return RoleAssignmentTest("sql",
            builder =>
            {
                var sql = builder.AddAzureSqlServer("sql")
                    .AddDatabase("db");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithReference(sql);
            },
            // scrub new lines since the test needs to run on Windows and Linux, and the new lines are different.
            s => s.Replace("\\r\\n", "\\n"));
    }

    [Fact]
    public Task KustoSupport()
    {
        return RoleAssignmentTest("kusto",
            builder =>
            {
                var kusto = builder.AddAzureKustoCluster("kusto");
                kusto.AddReadWriteDatabase("db1");
                kusto.AddReadWriteDatabase("db2");

                builder.AddProject<Project>("api", launchProfileName: null)
                    .WithReference(kusto);
            });
    }

    [Fact]
    public async Task ClearDefaultRoleAssignmentsRemovesDefaultAnnotation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var keyvault = builder.AddAzureKeyVault("keyvault")
            .ClearDefaultRoleAssignments();

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(keyvault);

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        // Verify that the DefaultRoleAssignmentsAnnotation was removed
        var keyVaultResource = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "keyvault");
        Assert.DoesNotContain(keyVaultResource.Annotations, a => a is DefaultRoleAssignmentsAnnotation);

        // Verify that no role assignment resources were created for the keyvault resource
        Assert.DoesNotContain(model.Resources, r => r.Name == "api-roles-keyvault");
    }

    [Fact]
    public async Task ClearDefaultRoleAssignmentsOnMultipleResources()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var keyvault = builder.AddAzureKeyVault("keyvault")
            .ClearDefaultRoleAssignments();

        var sb = builder.AddAzureServiceBus("sb")
            .ClearDefaultRoleAssignments();

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithReference(keyvault)
            .WithReference(sb);

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        // Verify that both resources had their DefaultRoleAssignmentsAnnotation removed
        var keyVaultResource = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "keyvault");
        Assert.DoesNotContain(keyVaultResource.Annotations, a => a is DefaultRoleAssignmentsAnnotation);

        var sbResource = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "sb");
        Assert.DoesNotContain(sbResource.Annotations, a => a is DefaultRoleAssignmentsAnnotation);

        // Verify that no role assignment resources were created
        Assert.DoesNotContain(model.Resources, r => r.Name == "api-roles-keyvault");
        Assert.DoesNotContain(model.Resources, r => r.Name == "api-roles-sb");
    }

    [Fact]
    public async Task ClearDefaultRoleAssignmentsDoesNotAffectExplicitRoleAssignments()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var keyvault = builder.AddAzureKeyVault("keyvault")
            .ClearDefaultRoleAssignments();

        builder.AddProject<Project>("api", launchProfileName: null)
            .WithRoleAssignments(keyvault, KeyVaultBuiltInRole.KeyVaultSecretsUser)
            .WithReference(keyvault);

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        // Verify that explicit role assignments still work even after ClearDefaultRoleAssignments
        var projRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "api-roles-keyvault");
        Assert.NotNull(projRoles);
    }

    [Fact]
    public async Task WaitForDoesNotCreateTransitiveRoleAssignments()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        var cache = builder.AddAzureManagedRedis("cache");

        var server = builder.AddProject<Project>("server", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .WithReference(cache)
            .WaitFor(cache);

        builder.AddProject<Project>("webfrontend", launchProfileName: null)
            .WithReference(server)
            .WaitFor(server);

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        // The server should have a role assignment to the cache since it directly references it
        Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "server-roles-cache");

        // The webfrontend should NOT have a role assignment to the cache since it only references the server
        Assert.DoesNotContain(model.Resources, r => r.Name == "webfrontend-roles-cache");
    }

    private static async Task RoleAssignmentTest(
        string azureResourceName,
        Action<IDistributedApplicationBuilder> configureBuilder,
        Func<string, string?>? scrubLines = null
        )
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        configureBuilder(builder);

        var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await ExecuteBeforeStartHooksAsync(app, default);

        var projRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == $"api-roles-{azureResourceName}");

        var (rolesManifest, rolesBicep) = await GetManifestWithBicep(projRoles);

        var verify = Verify(rolesManifest.ToString(), "json")
            .AppendContentAsFile(rolesBicep, "bicep");

        if (scrubLines is not null)
        {
            verify = verify.ScrubLinesWithReplace(scrubLines);
        }

        await verify;
    }

    private static Task<(JsonNode ManifestNode, string BicepText)> GetManifestWithBicep(IResource resource) =>
        AzureManifestUtils.GetManifestWithBicep(resource, skipPreparer: true);

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
