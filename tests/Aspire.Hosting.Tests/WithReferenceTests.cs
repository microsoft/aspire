// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class WithReferenceTests
{
    [Theory]
    [InlineData("mybinding")]
    [InlineData("MYbinding")]
    public async Task ResourceWithSingleEndpointProducesSimplifiedEnvironmentVariables(string endpointName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a binding and its matching annotation (simulating DCP behavior)
        var projectA = builder.AddProject<ProjectA>("projecta")
                .WithHttpsEndpoint(1000, 2000, "mybinding")
                .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        // Get the service provider.
        var projectB = builder.AddProject<ProjectB>("b").WithReference(projectA.GetEndpoint(endpointName));

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("https://localhost:2000", config["services__projecta__https__0"]);
        Assert.Equal("https://localhost:2000", config["PROJECTA_MYBINDING"]);

        Assert.True(projectB.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        var r = Assert.Single(relationships);
        Assert.Equal("Reference", r.Type);
        Assert.Same(projectA.Resource, r.Resource);
    }

    [Fact]
    public async Task ResourceNamesWithDashesAreEncodedInEnvironmentVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("project-a")
                .WithHttpsEndpoint(1000, 2000, "mybinding")
                .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        var projectB = builder.AddProject<ProjectB>("consumer")
            .WithReference(projectA);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("https://localhost:2000", config["services__project-a__https__0"]);
        Assert.Equal("https://localhost:2000", config["PROJECT_A_MYBINDING"]);
        Assert.DoesNotContain("services__project_a__https__0", config.Keys);
        Assert.DoesNotContain("PROJECT-A_MYBINDING", config.Keys);
    }

    [Fact]
    public async Task OverriddenServiceNamesAreEncodedInEnvironmentVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("project-a")
                .WithHttpsEndpoint(1000, 2000, "mybinding")
                .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        var projectB = builder.AddProject<ProjectB>("consumer")
            .WithReference(projectA, "custom-name");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("https://localhost:2000", config["services__custom-name__https__0"]);
        Assert.Equal("https://localhost:2000", config["custom_name_MYBINDING"]);
        Assert.DoesNotContain("services__custom_name__https__0", config.Keys);
        Assert.DoesNotContain("custom-name_MYBINDING", config.Keys);
    }

    [Theory]
    [InlineData(ReferenceEnvironmentInjectionFlags.All)]
    [InlineData(ReferenceEnvironmentInjectionFlags.ConnectionProperties)]
    [InlineData(ReferenceEnvironmentInjectionFlags.ConnectionString)]
    [InlineData(ReferenceEnvironmentInjectionFlags.ServiceDiscovery)]
    [InlineData(ReferenceEnvironmentInjectionFlags.Endpoints)]
    [InlineData(ReferenceEnvironmentInjectionFlags.None)]
    public async Task ResourceWithEndpointRespectsCustomEnvironmentVariableNaming(ReferenceEnvironmentInjectionFlags flags)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a binding and its matching annotation (simulating DCP behavior)
        var projectA = builder.AddProject<ProjectA>("projecta")
                .WithHttpsEndpoint(1000, 2000, "mybinding")
                .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        // Get the service provider.
        var projectB = builder.AddProject<ProjectB>("b")
            .WithReference(projectA, "custom")
            .WithReferenceEnvironment(flags);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        switch (flags)
        {
            case ReferenceEnvironmentInjectionFlags.All:
                Assert.Equal("https://localhost:2000", config["services__custom__https__0"]);
                Assert.Equal("https://localhost:2000", config["custom_MYBINDING"]);
                break;
            case ReferenceEnvironmentInjectionFlags.ConnectionProperties:
                Assert.False(config.ContainsKey("custom_MYBINDING"));
                Assert.False(config.ContainsKey("services__custom__https__0"));
                break;
            case ReferenceEnvironmentInjectionFlags.ConnectionString:
                Assert.False(config.ContainsKey("custom_MYBINDING"));
                Assert.False(config.ContainsKey("services__custom__https__0"));
                break;
            case ReferenceEnvironmentInjectionFlags.ServiceDiscovery:
                Assert.False(config.ContainsKey("custom_MYBINDING"));
                Assert.True(config.ContainsKey("services__custom__https__0"));
                break;
            case ReferenceEnvironmentInjectionFlags.Endpoints:
                Assert.True(config.ContainsKey("custom_MYBINDING"));
                Assert.False(config.ContainsKey("services__custom__https__0"));
                break;
            case ReferenceEnvironmentInjectionFlags.None:
                Assert.False(config.ContainsKey("custom_MYBINDING"));
                Assert.False(config.ContainsKey("services__custom__https__0"));
                break;
        }
    }

    [Fact]
    public async Task ResourceWithConflictingEndpointsProducesFullyScopedEnvironmentVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("projecta")
                              .WithHttpsEndpoint(1000, 2000, "mybinding")
                              .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000))
                              .WithHttpsEndpoint(1000, 3000, "myconflictingbinding")
                              // Create a binding and its matching annotation (simulating DCP behavior) - HOWEVER
                              // this binding conflicts with the earlier because they have the same scheme.
                              .WithEndpoint("myconflictingbinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 3000));

        var projectB = builder.AddProject<ProjectB>("projectb")
               .WithReference(projectA.GetEndpoint("mybinding"))
               .WithReference(projectA.GetEndpoint("myconflictingbinding"));

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("https://localhost:2000", config["services__projecta__https__0"]);
        Assert.Equal("https://localhost:3000", config["services__projecta__https__1"]);

        Assert.Equal("https://localhost:2000", config["PROJECTA_MYBINDING"]);
        Assert.Equal("https://localhost:3000", config["PROJECTA_MYCONFLICTINGBINDING"]);
    }

    [Fact]
    public async Task ResourceWithNonConflictingEndpointsProducesAllVariantsOfEnvironmentVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a binding and its matching annotation (simulating DCP behavior)
        var projectA = builder.AddProject<ProjectA>("projecta")
                              .WithHttpsEndpoint(1000, 2000, "mybinding")
                              .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000))
                              // Create a binding and its matching annotation (simulating DCP behavior) - not
                              // conflicting because the scheme is different to the first binding.
                              .WithHttpEndpoint(1000, 3000, "mynonconflictingbinding")
                              .WithEndpoint("mynonconflictingbinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 3000));

        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(projectA.GetEndpoint("mybinding"))
                              .WithReference(projectA.GetEndpoint("mynonconflictingbinding"));

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("https://localhost:2000", config["services__projecta__https__0"]);
        Assert.Equal("http://localhost:3000", config["services__projecta__http__0"]);

        Assert.Equal("https://localhost:2000", config["PROJECTA_MYBINDING"]);
        Assert.Equal("http://localhost:3000", config["PROJECTA_MYNONCONFLICTINGBINDING"]);
    }

    [Fact]
    public async Task ResourceWithConflictingEndpointsProducesAllEnvironmentVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a binding and its matching annotation (simulating DCP behavior)
        var projectA = builder.AddProject<ProjectA>("projecta")
                              .WithHttpsEndpoint(1000, 2000, "mybinding")
                              .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000))
                              .WithHttpsEndpoint(1000, 3000, "mybinding2")
                              .WithEndpoint("mybinding2", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 3000));

        // Get the service provider.
        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(projectA);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("https://localhost:2000", config["services__projecta__https__0"]);
        Assert.Equal("https://localhost:3000", config["services__projecta__https__1"]);

        Assert.Equal("https://localhost:2000", config["PROJECTA_MYBINDING"]);
        Assert.Equal("https://localhost:3000", config["PROJECTA_MYBINDING2"]);

        Assert.True(projectB.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        var r = Assert.Single(relationships);
        Assert.Equal("Reference", r.Type);
        Assert.Same(projectA.Resource, r.Resource);
    }

    [Fact]
    public async Task ResourceWithEndpointsProducesAllEnvironmentVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("projecta")
                              .WithHttpsEndpoint(1000, 2000, "mybinding")
                              .WithEndpoint("mybinding", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000))
                              .WithHttpEndpoint(1000, 3000, "mybinding2")
                              .WithEndpoint("mybinding2", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 3000));

        // Get the service provider.
        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(projectA);
        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("https://localhost:2000", config["services__projecta__https__0"]);
        Assert.Equal("http://localhost:3000", config["services__projecta__http__0"]);

        Assert.Equal("https://localhost:2000", config["PROJECTA_MYBINDING"]);
        Assert.Equal("http://localhost:3000", config["PROJECTA_MYBINDING2"]);

        Assert.True(projectB.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        var r = Assert.Single(relationships);
        Assert.Equal("Reference", r.Type);
        Assert.Same(projectA.Resource, r.Resource);
    }

    [Fact]
    public async Task InternalWithReferenceAddsConnectionStringAndServiceDiscoveryWhenResourceSupportsBoth()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var resource = builder.AddResource(new TestResourceWithConnectionStringAndServiceDiscovery("resource")
        {
            ConnectionString = "123"
        })
        .WithHttpEndpoint(1000, 2000, "http")
        .WithEndpoint("http", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        var projectB = builder.AddProject<ProjectB>("projectb");

        ResourceBuilderExtensions.WithReference(projectB, (IResourceBuilder<IResource>)resource, connectionName: "db", name: "api");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("123", config["ConnectionStrings__db"]);
        Assert.Equal("http://localhost:2000", config["services__api__http__0"]);
    }

    [Fact]
    public async Task InternalWithReferenceKeepsDefaultServiceDiscoveryNameWhenOnlyConnectionNameIsProvided()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var resource = builder.AddResource(new TestResourceWithConnectionStringAndServiceDiscovery("resource")
        {
            ConnectionString = "123"
        })
        .WithHttpEndpoint(1000, 2000, "http")
        .WithEndpoint("http", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 2000));

        var projectB = builder.AddProject<ProjectB>("projectb");

        ResourceBuilderExtensions.WithReference(projectB, (IResourceBuilder<IResource>)resource, connectionName: "db");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("123", config["ConnectionStrings__db"]);
        Assert.Equal("http://localhost:2000", config["services__resource__http__0"]);
        Assert.DoesNotContain("services__db__http__0", config.Keys);
    }

    [Fact]
    public async Task ConnectionStringResourceThrowsWhenMissingConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Get the service provider.
        var resource = builder.AddResource(new TestResource("resource"));
        var projectB = builder.AddProject<ProjectB>("projectb").WithReference(resource, optional: false);

        // Call environment variable callbacks.
        var aggregate = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        }).DefaultTimeout();

        var inner = Assert.IsType<DistributedApplicationException>(aggregate.InnerException);
        Assert.Equal("The connection string for the resource 'resource' is not available.", inner.Message);
    }

    [Fact]
    public async Task ConnectionStringResourceOptionalWithMissingConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Get the service provider.
        var resource = builder.AddResource(new TestResource("resource"));
        var projectB = builder.AddProject<ProjectB>("projectB")
                              .WithReference(resource, optional: true);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(0, servicesKeysCount);
    }

    [Fact]
    public async Task ParameterAsConnectionStringResourceThrowsWhenConnectionStringSectionMissing()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Get the service provider.
        var missingResource = builder.AddConnectionString("missingresource");
        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(missingResource);

        // Call environment variable callbacks.
        var aggregate = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        }).DefaultTimeout();

        var inner = Assert.IsType<MissingParameterValueException>(aggregate.InnerException);
        Assert.Equal("Connection string parameter resource could not be used because connection string 'missingresource' is missing.", inner.Message);
    }

    [Fact]
    public async Task ParameterAsConnectionStringResourceInjectsConnectionStringWhenPresent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.Configuration["ConnectionStrings:resource"] = "test connection string";

        // Get the service provider.
        var resource = builder.AddConnectionString("resource");
        var projectB = builder.AddProject<ProjectB>("projectb")
                             .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Equal("test connection string", config["ConnectionStrings__resource"]);
    }

    [Fact]
    public async Task ParameterAsConnectionStringResourceInjectsExpressionWhenPublishingManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Get the service provider.
        var resource = builder.AddConnectionString("resource");
        var projectB = builder.AddProject<ProjectB>("projectb")
                       .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Publish).DefaultTimeout();

        Assert.Equal("{resource.connectionString}", config["ConnectionStrings__resource"]);
    }

    [Fact]
    public async Task ParameterAsConnectionStringResourceInjectsCorrectEnvWhenPublishingManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Get the service provider.
        var resource = builder.AddConnectionString("resource", "MY_ENV");
        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Publish).DefaultTimeout();

        Assert.Equal("{resource.connectionString}", config["MY_ENV"]);
    }

    [Fact]
    public async Task ConnectionStringResourceWithConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Get the service provider.
        var resource = builder.AddResource(new TestResource("resource")
        {
            ConnectionString = "123"
        });
        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(1, servicesKeysCount);
        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__resource" && kvp.Value == "123");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectionStringResourceWithExpressionConnectionString(bool useFormat)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var endpoint = builder.AddParameter("endpoint", "http://localhost:3452");
        var key = builder.AddParameter("key", "secretKey", secret: true);

        // Ensure that parameters wrapped in IValueEncoder are added in ResourceRelationshipAnnotation.
        var cs = useFormat
            ? ReferenceExpression.Create($"Endpoint={endpoint};Key={key:uri}")
            : ReferenceExpression.Create($"Endpoint={endpoint};Key={key}")
            ;

        // Get the service provider.
        var resource = builder.AddConnectionString("cs", cs);

        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(1, servicesKeysCount);
        Assert.Equal("Endpoint=http://localhost:3452;Key=secretKey", config["ConnectionStrings__cs"]);

        Assert.True(projectB.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        var r = Assert.Single(relationships);
        Assert.Equal("Reference", r.Type);
        Assert.Same(resource.Resource, r.Resource);
        Assert.True(resource.Resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var csRelationships));
        Assert.Collection(csRelationships,
            r =>
            {
                Assert.Equal("WaitFor", r.Type);
                Assert.Same(endpoint.Resource, r.Resource);
            },
            r =>
            {
                Assert.Equal("WaitFor", r.Type);
                Assert.Same(key.Resource, r.Resource);
            },
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Same(endpoint.Resource, r.Resource);
            },
            r =>
            {
                Assert.Equal("Reference", r.Type);
                Assert.Same(key.Resource, r.Resource);
            });
    }

    [Fact]
    public async Task ConnectionStringResourceWithExpressionConnectionStringBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var endpoint = builder.AddParameter("endpoint", "http://localhost:3452");
        var key = builder.AddParameter("key", "secretKey", secret: true);

        // Get the service provider.
        var resource = builder.AddConnectionString("cs", b =>
        {
            b.Append($"Endpoint={endpoint};Key={key}");
        });

        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(1, servicesKeysCount);
        Assert.Equal("Endpoint=http://localhost:3452;Key=secretKey", config["ConnectionStrings__cs"]);
    }

    [Fact]
    public async Task ConnectionStringResourceWithConnectionStringOverwriteName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Get the service provider.
        var resource = builder.AddResource(new TestResource("resource")
        {
            ConnectionString = "123"
        });

        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(resource, connectionName: "bob");

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("ConnectionStrings__"));
        Assert.Equal(1, servicesKeysCount);
        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__bob" && kvp.Value == "123");
    }

    [Fact]
    public void WithReferenceHttpRelativeUriThrowsException()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        Assert.Throws<InvalidOperationException>(() => builder.AddProject<ProjectA>("projecta").WithReference("petstore", new Uri("petstore.swagger.io", UriKind.Relative)));
    }

    [Fact]
    public void WithReferenceHttpUriThrowsException()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        Assert.Throws<InvalidOperationException>(() => builder.AddProject<ProjectA>("projecta").WithReference("petstore", new Uri("https://petstore.swagger.io/v2")));
    }

    [Fact]
    public async Task WithReferenceHttpProduceEnvironmentVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var projectA = builder.AddProject<ProjectA>("projecta")
                               .WithReference("petstore", new Uri("https://petstore.swagger.io/"));

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectA.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        var servicesKeysCount = config.Keys.Count(k => k.StartsWith("services__"));
        Assert.Equal(1, servicesKeysCount);
        Assert.Contains(config, kvp => kvp.Key == "services__petstore__default__0" && kvp.Value == "https://petstore.swagger.io/");
        Assert.Contains(config, kvp => kvp.Key == "petstore" && kvp.Value == "https://petstore.swagger.io/");
    }

    [Fact]
    public async Task ProjectResourceWithReferenceGetsConnectionStringAndProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a test resource with connection properties
        var resource = builder.AddResource(new TestResourceWithProperties("resource")
        {
            ConnectionString = "Server=localhost;Database=mydb"
        });

        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        // Verify connection string is present
        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__resource" && kvp.Value == "Server=localhost;Database=mydb");

        // Verify connection properties are present (from the annotation - ProjectResource has All flag)
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_HOST" && kvp.Value == "localhost");
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_PORT" && kvp.Value == "5432");
    }

    [Fact]
    public async Task ExecutableResourceWithReferenceGetsConnectionStringAndProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a test resource with connection properties
        var resource = builder.AddResource(new TestResourceWithProperties("resource")
        {
            ConnectionString = "Server=localhost;Database=mydb"
        });

        var executable = builder.AddExecutable("myexe", "cmd", ".", args: [])
                                .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(executable.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        // Verify connection string is present
        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__resource" && kvp.Value == "Server=localhost;Database=mydb");

        // Verify connection properties are present (from the annotation - ExecutableResource has All flag)
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_HOST" && kvp.Value == "localhost");
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_PORT" && kvp.Value == "5432");
    }

    [Fact]
    public async Task JavaScriptAppAppResourceWithReferenceGetsConnectionStringAndProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a test resource with connection properties
        var resource = builder.AddResource(new TestResourceWithProperties("resource")
        {
            ConnectionString = "Server=localhost;Database=mydb"
        });

        var executable = builder.AddJavaScriptApp("NpmApp", ".\\app")
                                .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(executable.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        // Verify connection string is present
        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__resource" && kvp.Value == "Server=localhost;Database=mydb");

        // Verify connection properties are present (from the annotation - ExecutableResource has All flag)
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_HOST" && kvp.Value == "localhost");
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_PORT" && kvp.Value == "5432");
    }

    [Fact]
    public async Task PythonAppResourceWithReferenceGetsConnectionStringAndProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a test resource with connection properties
        var resource = builder.AddResource(new TestResourceWithProperties("resource")
        {
            ConnectionString = "Server=localhost;Database=mydb"
        });

#pragma warning disable CS0618
        var executable = builder.AddPythonApp("PythonApp", ".\\app", "app.py")
                                .WithReference(resource);
#pragma warning restore CS0618

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(executable.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        // Verify connection string is present
        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__resource" && kvp.Value == "Server=localhost;Database=mydb");

        // Verify connection properties are present (from the annotation - ExecutableResource has All flag)
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_HOST" && kvp.Value == "localhost");
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_PORT" && kvp.Value == "5432");
    }

    [Fact]
    public async Task ContainerResourceWithReferenceGetsOnlyConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a test resource with connection properties
        var resource = builder.AddResource(new TestResourceWithProperties("resource")
        {
            ConnectionString = "Server=localhost;Database=mydb"
        });

        var container = builder.AddContainer("mycontainer", "myimage")
                               .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        // Verify connection string is present
        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__resource" && kvp.Value == "Server=localhost;Database=mydb");

        // Verify connection properties are present (no annotation - defaults to has All flag)
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_HOST");
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_PORT");
    }

    [Fact]
    public async Task ResourceWithConnectionPropertiesExtensionRespectsFlags()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a test resource with connection properties
        var resource = builder.AddResource(new TestResourceWithProperties("resource")
        {
            ConnectionString = "Server=localhost;Database=mydb"
        });

        // Create a container and explicitly configure it to emit only connection properties
        var container = builder.AddContainer("mycontainer", "myimage")
                               .WithReferenceEnvironment(ReferenceEnvironmentInjectionFlags.ConnectionProperties)
                               .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(container.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        // Verify connection string is NOT present
        Assert.DoesNotContain(config, kvp => kvp.Key == "ConnectionStrings__resource");

        // Verify connection properties ARE present
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_HOST" && kvp.Value == "localhost");
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_PORT" && kvp.Value == "5432");
    }

    [Fact]
    public async Task ResourceWithConnectionPropertiesExtensionOverridesDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Create a test resource with connection properties
        var resource = builder.AddResource(new TestResourceWithProperties("resource")
        {
            ConnectionString = "Server=localhost;Database=mydb"
        });

        // Create a project resource and override the default injection flags
        // ProjectResource defaults to ReferenceEnvironmentInjectionFlags.All
        // Here we configure it to only inject ConnectionString (not ConnectionProperties)
        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReferenceEnvironment(ReferenceEnvironmentInjectionFlags.ConnectionString)
                              .WithReference(resource);

        // Call environment variable callbacks.
        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        // Verify connection string is present (included in ConnectionString flag)
        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__resource" && kvp.Value == "Server=localhost;Database=mydb");

        // Verify connection properties are NOT present (excluded by our custom annotation)
        Assert.DoesNotContain(config, kvp => kvp.Key == "RESOURCE_HOST");
        Assert.DoesNotContain(config, kvp => kvp.Key == "RESOURCE_PORT");
    }

    [Fact]
    public async Task ConnectionStringResourceWithConnectionPropertiesOverwriteName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var resource = builder.AddResource(new TestResourceWithProperties("resource")
        {
            ConnectionString = "Server=localhost;Database=mydb"
        });

        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(resource, connectionName: "bob");

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Contains(config, kvp => kvp.Key == "ConnectionStrings__bob" && kvp.Value == "Server=localhost;Database=mydb");
        Assert.Contains(config, kvp => kvp.Key == "BOB_HOST" && kvp.Value == "localhost");
        Assert.Contains(config, kvp => kvp.Key == "BOB_PORT" && kvp.Value == "5432");
        Assert.DoesNotContain(config, kvp => kvp.Key == "RESOURCE_HOST");
        Assert.DoesNotContain(config, kvp => kvp.Key == "RESOURCE_PORT");
    }

    [Fact]
    public async Task ConnectionPropertiesWithDashedNamesAreEncoded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var resource = builder.AddResource(new TestResourceWithProperties("resource-with-dash")
        {
            ConnectionString = "Server=localhost;Database=mydb"
        });

        var projectB = builder.AddProject<ProjectB>("projectb")
                              .WithReference(resource);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(projectB.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance).DefaultTimeout();

        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_WITH_DASH_HOST" && kvp.Value == "localhost");
        Assert.Contains(config, kvp => kvp.Key == "RESOURCE_WITH_DASH_PORT" && kvp.Value == "5432");
        Assert.DoesNotContain(config, kvp => kvp.Key == "RESOURCE-WITH-DASH_HOST");
        Assert.DoesNotContain(config, kvp => kvp.Key == "RESOURCE-WITH-DASH_PORT");
    }

    private sealed class TestResourceWithProperties(string name) : Resource(name), IResourceWithConnectionString
    {
        public string? ConnectionString { get; set; }

        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"{ConnectionString}");

        public IEnumerable<KeyValuePair<string, ReferenceExpression>> GetConnectionProperties()
        {
            yield return new KeyValuePair<string, ReferenceExpression>("Host", ReferenceExpression.Create($"localhost"));
            yield return new KeyValuePair<string, ReferenceExpression>("Port", ReferenceExpression.Create($"5432"));
        }
    }

    private sealed class TestResource(string name) : Resource(name), IResourceWithConnectionString
    {
        public string? ConnectionString { get; set; }

        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"{ConnectionString}");
    }

    private sealed class TestResourceWithConnectionStringAndServiceDiscovery(string name) : ContainerResource(name), IResourceWithConnectionString, IResourceWithServiceDiscovery
    {
        public string? ConnectionString { get; set; }

        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"{ConnectionString}");
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
}
