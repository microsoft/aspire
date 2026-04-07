// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class WithEnvironmentTests
{
    [Fact]
    public async Task BuiltApplicationHasAccessToIServiceProviderViaEnvironmentCallbackContext()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "image")
                               .WithEnvironment(context =>
                               {
                                   Assert.NotNull(context.Resource);

                                   var sp = context.ExecutionContext.ServiceProvider;
                                   context.EnvironmentVariables["SP_AVAILABLE"] = sp is not null ? "true" : "false";
                               });

        using var app = builder.Build();

        var serviceProvider = app.Services.GetRequiredService<IServiceProvider>();

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource,
            serviceProvider: serviceProvider
            ).DefaultTimeout();

        Assert.Equal("true", config["SP_AVAILABLE"]);
    }

    [Fact]
    public async Task EnvironmentReferencingEndpointPopulatesWithBindingUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("project")
                              .WithHttpsEndpoint(port: 1000, targetPort: 2000, "mybinding")
                              .WithEndpoint("mybinding", e =>
                              {
                                  e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000);
                              });

        var projectB = builder.AddProject<ProjectB>("projectB")
                               .WithEnvironment("myName", projectA.GetEndpoint("mybinding"));

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("https://localhost:2000", config["myName"]);

        Assert.True(projectB.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Collection(relationships,
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Same(projectA.Resource, r.Resource);
            });
    }

    [Fact]
    public async Task SimpleEnvironmentWithNameAndValue()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var project = builder.AddProject<ProjectA>("projectA")
            .WithEnvironment("myName", "value");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("value", config["myName"]);
    }

    [Fact]
    public async Task SimpleEnvironmentWithNameAndReferenceExpressionValue()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var childExpression = ReferenceExpression.Create($"value");
        var parameterExpression = ReferenceExpression.Create($"{childExpression}");

        var project = builder.AddProject<ProjectA>("projectA")
            .WithEnvironment("myName", parameterExpression);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("value", config["myName"]);
    }

    [Fact]
    public async Task EnvironmentCallbackPopulatesValueWhenCalled()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var environmentValue = "value";
        var projectA = builder.AddProject<ProjectA>("projectA").WithEnvironment("myName", () => environmentValue);

        environmentValue = "value2";

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectA.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("value2", config["myName"]);
    }

    [Fact]
    public async Task EnvironmentCallbackPopulatesValueWhenParameterResourceProvided()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["Parameters:parameter"] = "MY_PARAMETER_VALUE";

        var parameter = builder.AddParameter("parameter");

        var projectA = builder.AddProject<ProjectA>("projectA")
            .WithEnvironment("MY_PARAMETER", parameter);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectA.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("MY_PARAMETER_VALUE", config["MY_PARAMETER"]);

        Assert.True(projectA.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Collection(relationships,
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Same(parameter.Resource, r.Resource);
            });
    }

    [Fact]
    public async Task EnvironmentCallbackPopulatesWithExpressionPlaceholderWhenPublishingManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var parameter = builder.AddParameter("parameter");

        var projectA = builder.AddProject<ProjectA>("projectA")
            .WithEnvironment("MY_PARAMETER", parameter);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectA.Resource,
            DistributedApplicationOperation.Publish).DefaultTimeout();

        Assert.Equal("{parameter.value}", config["MY_PARAMETER"]);
    }

    [Fact]
    public async Task EnvironmentCallbackThrowsWhenParameterValueMissingInDcpMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var parameter = builder.AddParameter("parameter");

        var projectA = builder.AddProject<ProjectA>("projectA")
            .WithEnvironment("MY_PARAMETER", parameter);

        var exception = await Assert.ThrowsAsync<AggregateException>(async () => await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            projectA.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance
         )).DefaultTimeout();

        var innerException = Assert.IsType<MissingParameterValueException>(exception.InnerException);
        Assert.Equal("Parameter resource could not be used because configuration key 'Parameters:parameter' is missing and the Parameter has no default value.", innerException.Message);
    }

    [Fact]
    public async Task ComplexEnvironmentCallbackPopulatesValueWhenCalled()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var environmentValue = "value";
        var projectA = builder.AddProject<ProjectA>("projectA")
                              .WithEnvironment(context =>
                              {
                                  Assert.NotNull(context.Resource);

                                  context.EnvironmentVariables["myName"] = environmentValue;
                              });

        environmentValue = "value2";

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectA.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("value2", config["myName"]);
    }

    [Fact]
    public async Task ComplexAsyncEnvironmentCallbackPopulatesValueWhenCalled()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var environmentValue = "value";
        var projectA = builder.AddProject<ProjectA>("projectA")
                              .WithEnvironment(async context =>
                              {
                                  await Task.Yield();

                                  Assert.NotNull(context.Resource);

                                  context.EnvironmentVariables["myName"] = environmentValue;
                              });

        environmentValue = "value2";

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectA.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("value2", config["myName"]);
    }

    [Fact]
    public async Task EnvironmentVariableExpressions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var test = builder.AddResource(new TestResource("test", "connectionString"));

        var container = builder.AddContainer("container1", "image")
                               .WithHttpEndpoint(name: "primary", targetPort: 10005)
                               .WithEndpoint("primary", ep =>
                               {
                                   ep.AllocatedEndpoint = new AllocatedEndpoint(ep, "localhost", 17454);

                                   ep.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(ep, "container1.dev.internal", 10005, EndpointBindingMode.SingleAddress, networkID: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
                               });

        var endpoint = container.GetEndpoint("primary");

        var containerB = builder.AddContainer("container2", "imageB")
                                .WithEnvironment("URL", $"{endpoint}/foo")
                                .WithEnvironment("PORT", $"{endpoint.Property(EndpointProperty.Port)}")
                                .WithEnvironment("TARGET_PORT", $"{endpoint.Property(EndpointProperty.TargetPort)}")
                                .WithEnvironment("HOST", $"{test.Resource};name=1");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerB.Resource).DefaultTimeout();
        var manifestConfig = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerB.Resource, DistributedApplicationOperation.Publish).DefaultTimeout();

        Assert.Equal(4, config.Count);
        Assert.Equal($"http://container1.dev.internal:10005/foo", config["URL"]);
        Assert.Equal("10005", config["PORT"]);
        Assert.Equal("10005", config["TARGET_PORT"]);
        Assert.Equal("connectionString;name=1", config["HOST"]);

        Assert.Equal(4, manifestConfig.Count);
        Assert.Equal("{container1.bindings.primary.url}/foo", manifestConfig["URL"]);
        Assert.Equal("{container1.bindings.primary.port}", manifestConfig["PORT"]);
        Assert.Equal("{container1.bindings.primary.targetPort}", manifestConfig["TARGET_PORT"]);
        Assert.Equal("{test.connectionString};name=1", manifestConfig["HOST"]);

        Assert.True(containerB.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Collection(relationships.DistinctBy(r => (r.Resource, r.Type)),
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Same(container.Resource, r.Resource);
            },
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Same(test.Resource, r.Resource);
            });
    }

    [Fact]
    public void EnvironmentVariableSameResourceInSingleExpression()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container1", "image")
                               .WithHttpEndpoint(name: "primary", targetPort: 10005)
                               .WithEndpoint("primary", ep =>
                               {
                                   ep.AllocatedEndpoint = new AllocatedEndpoint(ep, "localhost", 90);
                               });

        var endpoint = container.GetEndpoint("primary");

        var containerB = builder.AddContainer("container2", "imageB")
                                .WithEnvironment("URL", $"{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}");

        Assert.True(containerB.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Collection(relationships,
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Same(container.Resource, r.Resource);
            });
    }

    [Fact]
    public async Task EnvironmentVariableWithDynamicTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container1", "image")
                               .WithHttpEndpoint(name: "primary")
                               .WithEndpoint("primary", ep =>
                               {
                                   ep.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(
                                       ep,
                                       "localhost",
                                       90,
                                       EndpointBindingMode.SingleAddress,
                                       """{{- portForServing "container1_primary" -}}""",
                                       KnownNetworkIdentifiers.DefaultAspireContainerNetwork
                                   ));
                               });

        var endpoint = container.GetEndpoint("primary");

        var containerB = builder.AddContainer("container2", "imageB")
                                .WithEnvironment("TARGET_PORT", $"{endpoint.Property(EndpointProperty.TargetPort)}");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(containerB.Resource).DefaultTimeout();

        var pair = Assert.Single(config);
        Assert.Equal("TARGET_PORT", pair.Key);
        Assert.Equal("""{{- portForServing "container1_primary" -}}""", pair.Value);
    }

    [Fact]
    public async Task EnvironmentWithConnectionStringSetsProperEnvironmentVariable()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();

        const string sourceCon = "sourceConnectionString";

        var sourceBuilder = builder.AddResource(new TestResource("sourceService", sourceCon));
        var targetBuilder = builder.AddContainer("targetContainer", "targetImage");

        string envVarName = "CUSTOM_CONNECTION_STRING";

        // Act
        targetBuilder.WithEnvironment(envVarName, sourceBuilder);

        // Call environment variable callbacks for the Run operation.
        var runConfig = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(targetBuilder.Resource, DistributedApplicationOperation.Run).DefaultTimeout();

        // Assert
        Assert.Single(runConfig, kvp => kvp.Key == envVarName && kvp.Value == sourceCon);

        // Call environment variable callbacks for the Publish operation.
        var publishConfig = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(targetBuilder.Resource, DistributedApplicationOperation.Publish).DefaultTimeout();

        // Assert
        Assert.Single(publishConfig, kvp => kvp.Key == envVarName && kvp.Value == "{sourceService.connectionString}");

        Assert.True(targetBuilder.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Collection(relationships,
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Same(sourceBuilder.Resource, r.Resource);
            });
    }

    [Fact]
    public async Task GenericWithEnvironmentWorksWithValueAndManifestProvider()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var testValue = new TestValueAndManifestProvider("test-runtime-value", "test-manifest-expression");
        var projectA = builder.AddProject<ProjectA>("projectA")
                              .WithEnvironment("TEST_VAR", testValue);

        // Call environment variable callbacks for runtime scenario
        var runtimeConfig = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            projectA.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("test-runtime-value", runtimeConfig["TEST_VAR"]);

        // Call environment variable callbacks for manifest scenario
        var manifestConfig = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            projectA.Resource,
            DistributedApplicationOperation.Publish,
            TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("test-manifest-expression", manifestConfig["TEST_VAR"]);
    }

    [Fact]
    public async Task GenericWithEnvironmentHandlesResourceReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var resourceA = builder.AddContainer("containerA", "imageA");
        var testValue = new TestValueWithReferences("test-value", resourceA.Resource);

        var projectA = builder.AddProject<ProjectA>("projectA")
                              .WithEnvironment("TEST_VAR", testValue);

        // Verify that the resource relationship was established
        Assert.True(projectA.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        var relationship = relationships.Single();
        Assert.Equal("Reference", relationship.Type);
        Assert.Same(resourceA.Resource, relationship.Resource);

        // Verify the environment variable is set correctly
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            projectA.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("test-value", config["TEST_VAR"]);
    }

    [Fact]
    public void GenericWithEnvironmentValidatesArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var projectA = builder.AddProject<ProjectA>("projectA");
        var testValue = new TestValueAndManifestProvider("value", "expression");

        // Test null builder
        Assert.Throws<ArgumentNullException>(() =>
            ResourceBuilderExtensions.WithEnvironment<ProjectResource, TestValueAndManifestProvider>(null!, "TEST_VAR", testValue));

        // Test null name
        Assert.Throws<ArgumentNullException>(() =>
            projectA.WithEnvironment<ProjectResource, TestValueAndManifestProvider>(null!, testValue));

        // Test null value
        Assert.Throws<ArgumentNullException>(() =>
            projectA.WithEnvironment("TEST_VAR", (TestValueAndManifestProvider)null!));
    }

    [Fact]
    public async Task GenericWithEnvironmentWorksWithEndpointProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container1", "image")
                               .WithHttpEndpoint(name: "primary", targetPort: 10005)
                               .WithEndpoint("primary", ep =>
                               {
                                   ep.AllocatedEndpoint = new AllocatedEndpoint(ep, "localhost", 90);
                               });

        var endpoint = container.GetEndpoint("primary");
        var portProperty = endpoint.Property(EndpointProperty.Port);

        var projectA = builder.AddProject<ProjectA>("projectA")
                              .WithEnvironment("ENDPOINT_PORT", portProperty);

        // Call environment variable callbacks for runtime scenario
        var runtimeConfig = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            projectA.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("90", runtimeConfig["ENDPOINT_PORT"]);

        // Call environment variable callbacks for manifest scenario
        var manifestConfig = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            projectA.Resource,
            DistributedApplicationOperation.Publish,
            TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("{container1.bindings.primary.port}", manifestConfig["ENDPOINT_PORT"]);

        // Verify that the resource relationship was established
        Assert.True(projectA.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        var relationship = relationships.Single();
        Assert.Equal("Reference", relationship.Type);
        Assert.Same(container.Resource, relationship.Resource);
    }

    private sealed class TestValueWithReferences : IValueProvider, IManifestExpressionProvider, IValueWithReferences
    {
        private readonly string _value;
        private readonly IResource _referencedResource;

        public TestValueWithReferences(string value, IResource referencedResource)
        {
            _value = value;
            _referencedResource = referencedResource;
        }

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<string?>(_value);
        }

        public string ValueExpression => _value;

        public IEnumerable<object> References => [_referencedResource];
    }

    private sealed class TestResource(string name, string connectionString) : Resource(name), IResourceWithConnectionString
    {
        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"{connectionString}");
    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";

        public LaunchSettings LaunchSettings { get; } = new();
    }

    private sealed class ProjectB : IProjectMetadata
    {
        public string ProjectPath => "projectB";
        public LaunchSettings LaunchSettings { get; } = new();
    }

    private sealed class TestValueAndManifestProvider : IValueProvider, IManifestExpressionProvider
    {
        private readonly string _runtimeValue;
        private readonly string _manifestExpression;

        public TestValueAndManifestProvider(string runtimeValue, string manifestExpression)
        {
            _runtimeValue = runtimeValue;
            _manifestExpression = manifestExpression;
        }

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<string?>(_runtimeValue);
        }

        public string ValueExpression => _manifestExpression;
    }
}
