// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Azure.Provisioning;
using Azure.Provisioning.Roles;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class AzureBicepResourceTests
{
    [Fact]
    public void AddBicepResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var bicepResource = builder.AddBicepTemplateString("mytemplate", "content")
                                   .WithParameter("param1", "value1")
                                   .WithParameter("param2", "value2");

        Assert.Equal("content", bicepResource.Resource.TemplateString);
        Assert.Equal("value1", bicepResource.Resource.Parameters["param1"]);
        Assert.Equal("value2", bicepResource.Resource.Parameters["param2"]);
    }

    public static TheoryData<Func<IDistributedApplicationBuilder, IResourceBuilder<IResource>>> AzureExtensions =>
        CreateAllAzureExtensions("x");

    private static TheoryData<Func<IDistributedApplicationBuilder, IResourceBuilder<IResource>>> CreateAllAzureExtensions(string resourceName)
    {
        static void CreateInfrastructure(AzureResourceInfrastructure infrastructure)
        {
            var id = new UserAssignedIdentity("id");
            infrastructure.Add(id);
            infrastructure.Add(new ProvisioningOutput("cid", typeof(string)) { Value = id.ClientId.ToBicepExpression() });
        }

        return new()
        {
            { builder => builder.AddAzureAppConfiguration(resourceName) },
            { builder => builder.AddAzureApplicationInsights(resourceName) },
            { builder => builder.AddBicepTemplate(resourceName, "template.bicep") },
            { builder => builder.AddBicepTemplateString(resourceName, "content") },
            { builder => builder.AddAzureInfrastructure(resourceName, CreateInfrastructure) },
            { builder => builder.AddAzureOpenAI(resourceName) },
            { builder => builder.AddAzureCosmosDB(resourceName) },
            { builder => builder.AddAzureEventHubs(resourceName) },
            { builder => builder.AddAzureKeyVault(resourceName) },
            { builder => builder.AddAzureLogAnalyticsWorkspace(resourceName) },
#pragma warning disable CS0618 // Type or member is obsolete
            { builder => builder.AddPostgres(resourceName).AsAzurePostgresFlexibleServer() },
            { builder => builder.AddRedis(resourceName).AsAzureRedis() },
            { builder => builder.AddSqlServer(resourceName).AsAzureSqlDatabase() },
            { builder => builder.AddAzureRedis(resourceName) },
#pragma warning restore CS0618 // Type or member is obsolete
            { builder => builder.AddAzurePostgresFlexibleServer(resourceName) },
            { builder => builder.AddAzureManagedRedis(resourceName) },
            { builder => builder.AddAzureSearch(resourceName) },
            { builder => builder.AddAzureServiceBus(resourceName) },
            { builder => builder.AddAzureSignalR(resourceName) },
            { builder => builder.AddAzureSqlServer(resourceName) },
            { builder => builder.AddAzureStorage(resourceName) },
            { builder => builder.AddAzureWebPubSub(resourceName) },
        };
    }

    [Theory]
    [MemberData(nameof(AzureExtensions))]
    public void AzureExtensionsAutomaticallyAddAzureProvisioning(Func<IDistributedApplicationBuilder, IResourceBuilder<IResource>> addAzureResource)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        addAzureResource(builder);

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
    }

    [Theory]
    [MemberData(nameof(AzureExtensions))]
    public void BicepResourcesAreIdempotent(Func<IDistributedApplicationBuilder, IResourceBuilder<IResource>> addAzureResource)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var azureResourceBuilder = addAzureResource(builder);

        if (azureResourceBuilder.Resource is not AzureProvisioningResource bicepResource)
        {
            // Skip
            return;
        }

        // This makes sure that these don't throw
        bicepResource.GetBicepTemplateFile();
        bicepResource.GetBicepTemplateFile();
    }

    public static TheoryData<Func<IDistributedApplicationBuilder, IResourceBuilder<IResource>>> AzureExtensionsWithHyphen =>
        CreateAllAzureExtensions("x-y");

    [Theory]
    [MemberData(nameof(AzureExtensionsWithHyphen))]
    public void AzureResourcesProduceValidBicep(Func<IDistributedApplicationBuilder, IResourceBuilder<IResource>> addAzureResource)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var azureResourceBuilder = addAzureResource(builder);

        if (azureResourceBuilder.Resource is not AzureProvisioningResource bicepResource)
        {
            // Skip
            return;
        }

        var bicep = bicepResource.GetBicepTemplateString();

        Assert.DoesNotContain("resource x-y", bicep);
    }

    [Fact]
    public void GetOutputReturnsOutputValue()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var bicepResource = builder.AddBicepTemplateString("templ", "content");

        bicepResource.Resource.Outputs["resourceEndpoint"] = "https://myendpoint";

        Assert.Equal("https://myendpoint", bicepResource.GetOutput("resourceEndpoint").Value);
    }

    [Fact]
    public void GetSecretOutputReturnsSecretOutputValue()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var bicepResource = builder.AddBicepTemplateString("templ", "content");

        bicepResource.Resource.SecretOutputs["connectionString"] = "https://myendpoint;Key=43";

#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal("https://myendpoint;Key=43", bicepResource.GetSecretOutput("connectionString").Value);
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Fact]
    public void GetOutputValueThrowsIfNoOutput()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var bicepResource = builder.AddBicepTemplateString("templ", "content");

        Assert.Throws<InvalidOperationException>(() => bicepResource.GetOutput("resourceEndpoint").Value);
    }

    [Fact]
    public void GetSecretOutputValueThrowsIfNoOutput()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var bicepResource = builder.AddBicepTemplateString("templ", "content");

#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Throws<InvalidOperationException>(() => bicepResource.GetSecretOutput("connectionString").Value);
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Fact]
    public async Task AssertManifestLayout()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var param = builder.AddParameter("p1");

        var b2 = builder.AddBicepTemplateString("temp2", "content");

        var bicepResource = builder.AddBicepTemplateString("templ", "content")
                                    .WithParameter("param1", "value1")
                                    .WithParameter("param2", ["1", "2"])
                                    .WithParameter("param3", new JsonObject() { ["value"] = "nested" })
                                    .WithParameter("param4", param)
                                    .WithParameter("param5", b2.GetOutput("value1"))
                                    .WithParameter("param6", () => b2.GetOutput("value2"));

        bicepResource.Resource.TempDirectory = Environment.CurrentDirectory;

        var manifest = await ManifestUtils.GetManifest(bicepResource.Resource);

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "path": "templ.module.bicep",
              "params": {
                "param1": "value1",
                "param2": [
                  "1",
                  "2"
                ],
                "param3": {
                  "value": "nested"
                },
                "param4": "{p1.value}",
                "param5": "{temp2.outputs.value1}",
                "param6": "{temp2.outputs.value2}"
              }
            }
            """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Fact]
    public async Task BicepResourceHasPipelineStepAnnotationWithCorrectConfiguration()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        var bicepResource = builder.AddBicepTemplateString("myresource", "content");

        // Act - Get the annotation
        var annotation = bicepResource.Resource.Annotations.OfType<Aspire.Hosting.Pipelines.PipelineStepAnnotation>().FirstOrDefault();
        
        // Assert - Annotation exists
        Assert.NotNull(annotation);
        
        // Act - Create the step from the annotation
        var factoryContext = new Aspire.Hosting.Pipelines.PipelineStepFactoryContext
        {
            PipelineContext = null!, // Not needed for this test
            Resource = bicepResource.Resource
        };
        var steps = await annotation.CreateStepsAsync(factoryContext);
        var step = steps.First();

        // Assert - Step has correct name
        Assert.Equal("provision-myresource", step.Name);
        
        // Assert - Step is configured with RequiredBy relationship to ProvisionInfrastructure
        // Note: RequiredBy relationships are stored internally and converted to DependsOn during pipeline execution
        // This test verifies the step is created correctly; the conversion is tested in pipeline tests
        
        // Assert - Step depends on CreateProvisioningContext
        Assert.Contains(AzureEnvironmentResource.CreateProvisioningContextStepName, step.DependsOnSteps);
    }

    [Fact]
    public void GetBicepTemplateFile_WithTemplateFile_ReturnsOriginalPathWhenDirectoryProvided()
    {
        // This test verifies the fix for https://github.com/microsoft/aspire/issues/13967
        // When a templateFile is specified, GetBicepTemplateFile should return the original path
        // and not combine it with the directory parameter.

        using var tempDir = new TestTempDirectory();

        // Create a test bicep file
        var bicepFileName = "test-template.bicep";
        var bicepFilePath = Path.Combine(tempDir.Path, bicepFileName);
        File.WriteAllText(bicepFilePath, "param location string = resourceGroup().location");

        // Create the AzureBicepResource with the templateFile
        var resource = new AzureBicepResource("test-resource", templateFile: bicepFilePath);

        // Create a different directory to pass to GetBicepTemplateFile
        var outputDir = Path.Combine(tempDir.Path, "output");
        Directory.CreateDirectory(outputDir);

        // Get the bicep template file with a directory parameter
        using var templateFile = resource.GetBicepTemplateFile(outputDir);

        // The path should be the original template file path, not combined with outputDir
        Assert.Equal(bicepFilePath, templateFile.Path);
        Assert.True(File.Exists(templateFile.Path), $"The template file should exist at {templateFile.Path}");
    }

    [Fact]
    public void GetBicepTemplateFile_WithTemplateFile_ReturnsOriginalPathWithoutDirectory()
    {
        using var tempDir = new TestTempDirectory();

        // Create a test bicep file
        var bicepFileName = "test-template.bicep";
        var bicepFilePath = Path.Combine(tempDir.Path, bicepFileName);
        File.WriteAllText(bicepFilePath, "param location string = resourceGroup().location");

        // Create the AzureBicepResource with the templateFile
        var resource = new AzureBicepResource("test-resource", templateFile: bicepFilePath);

        // Get the bicep template file without a directory parameter
        using var templateFile = resource.GetBicepTemplateFile();

        // The path should be the original template file path
        Assert.Equal(bicepFilePath, templateFile.Path);
    }

    [Fact]
    public void GetBicepTemplateFile_WithTemplateString_WritesToDirectory()
    {
        using var tempDir = new TestTempDirectory();

        var bicepContent = "param location string = resourceGroup().location";

        // Create the AzureBicepResource with a template string (not a file)
        var resource = new AzureBicepResource("test-resource", templateString: bicepContent);

        // Create a directory to pass to GetBicepTemplateFile
        var outputDir = Path.Combine(tempDir.Path, "output");
        Directory.CreateDirectory(outputDir);

        // Get the bicep template file with a directory parameter
        using var templateFile = resource.GetBicepTemplateFile(outputDir);

        // The path should be in the output directory
        Assert.StartsWith(outputDir, templateFile.Path);
        Assert.True(File.Exists(templateFile.Path), $"The template file should exist at {templateFile.Path}");
        Assert.Equal(bicepContent, File.ReadAllText(templateFile.Path));
    }
}
