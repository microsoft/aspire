// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Azd.Tests;

/// <summary>
/// Verifies that an imported azd project behaves natively end-to-end: it runs locally on emulators and
/// containers, it reuses the customer's azd environment, and it produces deployable Azure infrastructure
/// on publish.
/// </summary>
public class NativeIntegrationTests
{
    [Fact]
    public void RunModeRunsLocalResourcesAsEmulatorsAndContainers()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        // Resources with an emulator run locally without touching Azure.
        Assert.True(import.Resources["files"].Resource.IsEmulator(), "Storage should run on the Azurite emulator locally.");
        Assert.True(import.Resources["orders"].Resource.IsEmulator(), "Cosmos DB should run on the emulator locally.");
        Assert.True(import.Resources["sb"].Resource.IsEmulator(), "Service Bus should run on the emulator locally.");

        // Resources without an emulator run as containers; RunAsContainer swaps the Azure resource for a
        // same-named container, so the container is observable on the application model.
        Assert.Contains(builder.Resources, r => string.Equals(r.Name, "cache", StringComparison.Ordinal) && r.IsContainer());
        Assert.Contains(builder.Resources, r => string.Equals(r.Name, "pg", StringComparison.Ordinal) && r.IsContainer());
    }

    [Fact]
    public void PublishModeKeepsAzureResourcesForDeployment()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var import = builder.AddAzdProject(sample.AzureYamlPath);

        // Publish targets the real Azure resources, so nothing is swapped for an emulator or container.
        Assert.False(import.Resources["files"].Resource.IsEmulator());
        Assert.False(import.Resources["orders"].Resource.IsEmulator());
        Assert.DoesNotContain(builder.Resources, r => string.Equals(r.Name, "cache", StringComparison.Ordinal) && r.IsContainer());
        Assert.DoesNotContain(builder.Resources, r => string.Equals(r.Name, "pg", StringComparison.Ordinal) && r.IsContainer());

        // The Azure resources (and therefore their generated infrastructure) are still present.
        Assert.Contains(builder.Resources.OfType<AzureManagedRedisResource>(), r => string.Equals(r.Name, "cache", StringComparison.Ordinal));
        Assert.Contains(builder.Resources.OfType<AzurePostgresFlexibleServerResource>(), r => string.Equals(r.Name, "pg", StringComparison.Ordinal));
    }

    [Fact]
    public void ReusesAzureEnvironmentForProvisioning()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddAzdProject(sample.AzureYamlPath);

        // The subscription/location/resource group recorded by azd become the Aspire provisioning target,
        // so a migrated app deploys into the same place instead of re-prompting or creating a parallel env.
        Assert.Equal("00000000-0000-0000-0000-000000000000", builder.Configuration["Azure:SubscriptionId"]);
        Assert.Equal("eastus2", builder.Configuration["Azure:Location"]);
        Assert.Equal("rg-contoso-dev", builder.Configuration["Azure:ResourceGroup"]);
        Assert.Equal("22222222-2222-2222-2222-222222222222", builder.Configuration["Azure:TenantId"]);
    }

    [Fact]
    public void ReuseAzureEnvironmentDoesNotOverwriteConfiguredValues()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        // A value the app host author already set must win over the reused azd environment.
        builder.Configuration["Azure:Location"] = "westeurope";

        builder.AddAzdProject(sample.AzureYamlPath);

        Assert.Equal("westeurope", builder.Configuration["Azure:Location"]);
        // Keys the author did not set are still filled in from the azd environment.
        Assert.Equal("rg-contoso-dev", builder.Configuration["Azure:ResourceGroup"]);
    }

    [Fact]
    public void DisablingEmulatorsKeepsAzureResourcesInRunMode()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        var import = builder.AddAzdProject(sample.AzureYamlPath, options => options.UseEmulatorsForLocalRun = false);

        // Opting out leaves the Azure resources in place even when running locally.
        Assert.False(import.Resources["files"].Resource.IsEmulator());
        Assert.DoesNotContain(builder.Resources, r => string.Equals(r.Name, "cache", StringComparison.Ordinal) && r.IsContainer());
        Assert.Contains(builder.Resources.OfType<AzureManagedRedisResource>(), r => string.Equals(r.Name, "cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishGeneratesDeployableBicepForMappedResources()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Focus the proof on the data resources; compute environments add deployment-target wiring that is
        // orthogonal to "do the imported resources emit deployable infrastructure?".
        var import = builder.AddAzdProject(sample.AzureYamlPath, options => options.CreateComputeEnvironments = false);

        // Build the model from just the Azure resources so the bicep preparer does not require a compute
        // environment for the imported services.
        var azureModel = new DistributedApplicationModel(
            builder.Resources.Where(r => r is not ProjectResource and not ContainerResource).ToList());

        var (_, cosmosBicep) = await AzureManifestUtils.GetManifestWithBicep(azureModel, import.Resources["orders"].Resource);
        var (_, serviceBusBicep) = await AzureManifestUtils.GetManifestWithBicep(azureModel, import.Resources["sb"].Resource);

        // The imported resources produce real Aspire-generated ARM, which is what makes the migrated app
        // deployable with `aspire publish` / `aspire deploy`.
        Assert.Contains("Microsoft.DocumentDB/databaseAccounts", cosmosBicep);
        Assert.Contains("Microsoft.ServiceBus/namespaces", serviceBusBicep);
    }

    [Fact]
    public void RedisIsMappedToAzureManagedRedisWithASkuSubstitutionWarning()
    {
        var root = Directory.CreateTempSubdirectory("azd-import-redis-sku-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "azure.yaml"),
                """
                name: redis-app
                resources:
                  cache:
                    type: db.redis
                """);

            using var builder = TestDistributedApplicationBuilder.Create();
            var import = builder.AddAzdProject(root.FullName);

            // azd's db.redis is Azure Cache for Redis (Microsoft.Cache/redis), which Azure is retiring.
            // Aspire maps it to its supported successor, Azure Managed Redis, and surfaces the SKU change
            // as a warning rather than silently swapping products.
            Assert.IsType<AzureManagedRedisResource>(import.Resources["cache"].Resource);
            Assert.Contains(
                import.Diagnostics.Items,
                d => d.Severity == AzdImportDiagnosticSeverity.Warning
                    && d.Target == "cache"
                    && d.Message.Contains("Azure Managed Redis"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

#pragma warning disable ASPIREAZURE001
    [Fact]
    public async Task ReusesAzureEnvironmentForTheAzureEnvironmentResource()
    {
        using var sample = SampleAzdProject.Create();
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddAzdProject(sample.AzureYamlPath);

        // AddAzureProvisioning (invoked when the imported Azure resources are added) always adds an
        // AzureEnvironmentResource. Its Location/ResourceGroupName/PrincipalId parameters are the canonical
        // deployment target the generated bicep is parameterized on, so reusing the azd environment must
        // populate them; otherwise resolving them throws MissingParameterValueException and a grown-up app
        // would prompt for values azd already recorded.
        var environment = Assert.Single(builder.Resources.OfType<Aspire.Hosting.Azure.AzureEnvironmentResource>());

        Assert.Equal("eastus2", await environment.Location.GetValueAsync(default));
        Assert.Equal("rg-contoso-dev", await environment.ResourceGroupName.GetValueAsync(default));
        Assert.Equal("11111111-1111-1111-1111-111111111111", await environment.PrincipalId.GetValueAsync(default));
    }
#pragma warning restore ASPIREAZURE001
}
