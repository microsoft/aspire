// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRESERVICECONTRACTS001

using System.Text.Json.Nodes;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ServiceContractTests(ITestOutputHelper outputHelper)
{
    private const string Contract = """
        {
          "schemaVersion": 1, "id": "contoso.catalog", "version": "1.0.0",
          "image": "registry.example/catalog", "imageTag": "1.0.0",
          "project": "Catalog.csproj",
          "http": { "targetPort": 8080, "portEnvironmentVariable": "HTTP_PORT", "readinessPath": "/health" },
          "inputs": [{ "name": "token", "environmentVariable": "CATALOG_TOKEN" }]
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothImplementationsPreserveEndpointHealthAndParameterReferences(bool source)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;
        var artifact = Path.Combine(root, "aspire-service.json");
        await File.WriteAllTextAsync(artifact, Contract);
        await File.WriteAllTextAsync(Path.Combine(root, "Catalog.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        using var builder = TestDistributedApplicationBuilder.Create();
        var token = builder.AddParameter("token", secret: true);
        var resource = builder.AddServiceContract("catalog", artifact, "contoso.catalog", 1,
            new Dictionary<string, IResourceBuilder<ParameterResource>> { ["token"] = token },
            source ? root : null);

        if (source)
        {
            Assert.IsType<ProjectResource>(resource.Resource);
        }
        else
        {
            var container = Assert.IsType<ContainerResource>(resource.Resource);
            Assert.True(container.TryGetContainerImageName(out var image));
            Assert.Equal("registry.example/catalog:1.0.0", image);
        }
        var endpoint = Assert.Single(resource.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);
        Assert.Equal(source ? null : (int?)8080, endpoint.TargetPort);
        Assert.Null(endpoint.TargetPortEnvironmentVariable);
        Assert.NotEmpty(resource.Resource.Annotations.OfType<HealthCheckAnnotation>());

        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource.Resource, environment);
        foreach (var callback in resource.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }
        Assert.Same(token.Resource, environment["CATALOG_TOKEN"]);
        var port = Assert.IsType<EndpointReferenceExpression>(environment["HTTP_PORT"]);
        Assert.Equal(EndpointProperty.TargetPort, port.Property);
        Assert.Same(resource.Resource, port.Endpoint.Resource);

        var consumer = builder.AddExecutable("consumer", "test", root)
            .WithReference(resource.GetEndpoint("http")).WaitFor(resource);
        Assert.Contains(consumer.Resource.Annotations.OfType<WaitAnnotation>(),
            annotation => ReferenceEquals(annotation.Resource, resource.Resource));
    }

    [Fact]
    public async Task SourceKestrelEndpointRetainsAllocatedUrlOverrideAndContractPort()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;
        var path = Path.Combine(root, "aspire-service.json");
        await File.WriteAllTextAsync(path, Contract);
        await File.WriteAllTextAsync(Path.Combine(root, "Catalog.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), """
            { "Kestrel": { "Endpoints": { "http": { "Url": "http://localhost:54321" } } } }
            """);
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddServiceContract("catalog", path, "contoso.catalog", 1,
            new Dictionary<string, IResourceBuilder<ParameterResource>> { ["token"] = builder.AddParameter("token") }, root);
        var endpoint = Assert.Single(resource.Resource.Annotations.OfType<EndpointAnnotation>());
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 54321, targetPortExpression: "45678");

        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource.Resource, environment);
        foreach (var callback in resource.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        var kestrelUrl = Assert.IsType<ReferenceExpression>(environment["Kestrel__Endpoints__http__Url"]);
        Assert.Equal("http://localhost:45678", await kestrelUrl.GetValueAsync(default));
        var port = Assert.IsType<EndpointReferenceExpression>(environment["HTTP_PORT"]);
        Assert.Equal("45678", await port.GetValueAsync(default));
    }

    [Theory]
    [InlineData("id", "\"other\"", "expected 'contoso.catalog'")]
    [InlineData("version", "\"2.0.0\"", "expected 'contoso.catalog'")]
    [InlineData("schemaVersion", "2", "schemaVersion must be 1")]
    [InlineData("version", "\"1.0\"", "three-part numeric version")]
    [InlineData("imageTag", "\"latest\"", "explicit image/tag")]
    [InlineData("imageTag", "null", "explicit image/tag")]
    [InlineData("imageTag", "\"1.0.0/typo\"", "explicit image/tag")]
    [InlineData("imageTag", "\"-invalid\"", "explicit image/tag")]
    [InlineData("imageTag", "\".invalid\"", "explicit image/tag")]
    [InlineData("imageTag", "\"has space\"", "explicit image/tag")]
    [InlineData("imageTag", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "explicit image/tag")]
    [InlineData("image", "\"https://registry.example/catalog\"", "invalid container image")]
    [InlineData("http", "null", "HTTP target port")]
    [InlineData("http", """{"targetPort":0,"portEnvironmentVariable":"PORT","readinessPath":"/health"}""", "HTTP target port")]
    [InlineData("http", """{"targetPort":8080,"portEnvironmentVariable":"BAD-NAME","readinessPath":"/health"}""", "HTTP target port")]
    [InlineData("http", """{"targetPort":8080,"portEnvironmentVariable":"PORT","readinessPath":"//other/health"}""", "absolute readiness path")]
    [InlineData("inputs", "null", "unique input names")]
    [InlineData("inputs", "[null]", "unique input names")]
    [InlineData("inputs", """[{"name":"token","environmentVariable":"HTTP_PORT"}]""", "unique input names")]
    [InlineData("inputs", """[{"name":"token","environmentVariable":"A"},{"name":"token","environmentVariable":"B"}]""", "unique input names")]
    [InlineData("inputs", """[{"name":"one","environmentVariable":"A"},{"name":"two","environmentVariable":"a"}]""", "unique input names")]
    [InlineData("unknown", "true", "Invalid service contract")]
    public async Task RejectsInvalidContractsBeforeAddingResources(string property, string value, string expected)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "contract.json");
        var contract = JsonNode.Parse(Contract)!;
        contract[property] = JsonNode.Parse(value);
        await File.WriteAllTextAsync(path, contract.ToJsonString());
        using var builder = TestDistributedApplicationBuilder.Create();
        var token = builder.AddParameter("token", secret: true);
        var count = builder.Resources.Count;

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddServiceContract("catalog", path, "contoso.catalog", 1,
                new Dictionary<string, IResourceBuilder<ParameterResource>> { ["token"] = token }));

        Assert.Contains(expected, exception.Message);
        Assert.Equal(count, builder.Resources.Count);
    }

    [Fact]
    public async Task ReportsMissingAndUnexpectedBindingsWithoutMutatingModel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "contract.json");
        await File.WriteAllTextAsync(path, Contract);
        using var builder = TestDistributedApplicationBuilder.Create();
        var token = builder.AddParameter("token", secret: true);

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddServiceContract("catalog", path, "contoso.catalog", 1,
                new Dictionary<string, IResourceBuilder<ParameterResource>> { ["typo"] = token }));

        Assert.Contains("Missing: [token]. Unexpected: [typo]", exception.Message);
        Assert.DoesNotContain(builder.Resources, resource => resource.Name == "catalog");
    }

    [Fact]
    public async Task LocalCheckoutUsesProducerMetadataAndRejectsBreakingMajor()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;
        var artifact = Path.Combine(root, "released.json");
        var checkout = Path.Combine(root, "producer");
        Directory.CreateDirectory(checkout);
        var local = JsonNode.Parse(Contract)!;
        local["version"] = "1.1.0";
        local["project"] = "Moved.csproj";
        await File.WriteAllTextAsync(artifact, Contract);
        await File.WriteAllTextAsync(Path.Combine(checkout, "aspire-service.json"), local.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(checkout, "Moved.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        using var builder = TestDistributedApplicationBuilder.Create(options => options.ProjectDirectory = root);
        var inputs = new Dictionary<string, IResourceBuilder<ParameterResource>>
        {
            ["token"] = builder.AddParameter("token", secret: true)
        };
        var resource = builder.AddServiceContract("catalog", "released.json", "contoso.catalog", 1, inputs, "producer");
        var project = Assert.IsType<ProjectResource>(resource.Resource);
        Assert.True(project.TryGetProjectMetadata(out var metadata));
        Assert.Equal(Path.Combine(checkout, "Moved.csproj"), metadata.ProjectPath);

        local["version"] = "2.0.0";
        await File.WriteAllTextAsync(Path.Combine(checkout, "aspire-service.json"), local.ToJsonString());
        Assert.Throws<DistributedApplicationException>(() =>
            builder.AddServiceContract("breaking", artifact, "contoso.catalog", 1, inputs, checkout));
    }

    [Theory]
    [InlineData("../outside.csproj", "project must be inside")]
    [InlineData(@"..\outside.csproj", "project must be inside")]
    [InlineData(@"src/..\..\outside.csproj", "project must be inside")]
    [InlineData("Missing.csproj", "does not exist")]
    public async Task RejectsInvalidSourceProject(string project, string expected)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;
        var contract = JsonNode.Parse(Contract)!;
        contract["project"] = project;
        var path = Path.Combine(root, "aspire-service.json");
        await File.WriteAllTextAsync(path, contract.ToJsonString());
        using var builder = TestDistributedApplicationBuilder.Create();
        var exception = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddServiceContract("catalog", path, "contoso.catalog", 1,
                new Dictionary<string, IResourceBuilder<ParameterResource>> { ["token"] = builder.AddParameter("token") }, root));

        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain(builder.Resources, resource => resource.Name == "catalog");
    }

    [Theory]
    [InlineData(@"src\Catalog\Catalog.csproj")]
    [InlineData(@"src/Catalog\Catalog.csproj")]
    public async Task SourceProjectUsesNativeProjectSeparatorNormalization(string project)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;
        var directory = Directory.CreateDirectory(Path.Combine(root, "src", "Catalog"));
        var expectedPath = Path.Combine(directory.FullName, "Catalog.csproj");
        await File.WriteAllTextAsync(expectedPath, "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        var contract = JsonNode.Parse(Contract)!;
        contract["project"] = project;
        var path = Path.Combine(root, "aspire-service.json");
        await File.WriteAllTextAsync(path, contract.ToJsonString());
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddServiceContract("catalog", path, "contoso.catalog", 1,
            new Dictionary<string, IResourceBuilder<ParameterResource>> { ["token"] = builder.AddParameter("token") }, root);

        Assert.True(Assert.IsType<ProjectResource>(resource.Resource).TryGetProjectMetadata(out var metadata));
        Assert.Equal(expectedPath, metadata.ProjectPath);
    }

    [Fact]
    public async Task RejectsParametersFromAnotherModel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "contract.json");
        await File.WriteAllTextAsync(path, Contract);
        using var builder = TestDistributedApplicationBuilder.Create();
        using var other = TestDistributedApplicationBuilder.Create();
        var token = other.AddParameter("token");
        var exception = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddServiceContract("catalog", path, "contoso.catalog", 1,
                new Dictionary<string, IResourceBuilder<ParameterResource>> { ["token"] = token }));

        Assert.Contains("must be a parameter from this AppHost", exception.Message);
        Assert.Empty(builder.Resources);
    }
}
