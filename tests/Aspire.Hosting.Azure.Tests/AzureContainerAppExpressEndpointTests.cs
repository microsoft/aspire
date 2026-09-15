// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREACAEXPRESS001
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureContainerAppExpressEndpointTests(ITestOutputHelper outputHelper)
{
    private const string DomainOutputName = "AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN";

    [Fact]
    public async Task PublicReferencesUseEnvironmentDomainAndPreserveSelfTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var enabled = builder.AddParameter("enabled");
        var api = builder.AddProject("api", "api.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        var web = builder.AddProject("web", "web.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint()
            .WithReference(api);
        var endpoint = api.GetEndpoint("http");
        web.WithEnvironment(context =>
        {
            foreach (var property in new[]
            {
                EndpointProperty.Url, EndpointProperty.Host, EndpointProperty.IPV4Host,
                EndpointProperty.HostAndPort, EndpointProperty.Port, EndpointProperty.TargetPort,
                EndpointProperty.Scheme, EndpointProperty.TlsEnabled
            })
            {
                context.EnvironmentVariables[property.ToString().ToUpperInvariant()] = endpoint.Property(property);
            }

            var nested = ReferenceExpression.Create($"prefix/{ReferenceExpression.Create($"{endpoint}/health")}");
            context.EnvironmentVariables["CONDITIONAL"] = ReferenceExpression.CreateConditional(
                enabled.Resource, bool.TrueString, nested, ReferenceExpression.Create($"disabled"));
            context.EnvironmentVariables["SELF_TARGET_PORT"] = web.GetEndpoint("http").Property(EndpointProperty.TargetPort);
        });
        web.WithArgs(context => context.Args.Add(ReferenceExpression.Create($"--api={endpoint}")));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var apiTarget = GetTarget(api.Resource);
        var webTarget = GetTarget(web.Resource);
        var (manifest, bicep) = await GetManifestWithBicep(webTarget, skipPreparer: true);
        var (_, apiBicep) = await GetManifestWithBicep(apiTarget, skipPreparer: true);

        // The public hostname comes from the environment's default domain, so a consumer never
        // depends on the producing app's own deployment outputs.
        var domain = Assert.Single(webTarget.Parameters.Values.OfType<BicepOutputReference>(), output => output.Name == DomainOutputName);
        Assert.Same(environment.Resource, domain.Resource);
        Assert.DoesNotContain(webTarget.Parameters.Values.OfType<BicepOutputReference>(), output => output.Resource == apiTarget);
        Assert.False(endpoint.IsAllocated);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep")
            .AppendContentAsFile(apiBicep, "bicep", "api");
    }

    [Fact]
    public async Task EndpointPropertyExpressionsResolveFromTheEnvironmentDomain()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var api = builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        var endpoint = api.GetEndpoint("http");
        var host = environment.Resource.GetHostAddressExpression(endpoint);
        var output = Assert.IsType<BicepOutputReference>(Assert.Single(host.ValueProviders));
        Assert.Equal(DomainOutputName, output.Name);
        Assert.Same(environment.Resource, output.Resource);
        Assert.Null(api.Resource.GetDeploymentTargetAnnotation());

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var target = GetTarget(api.Resource);
        await ExecuteBeforeStartHooksAsync(app, default);
        Assert.Same(target, GetTarget(api.Resource));
        Assert.Single(api.Resource.Annotations.OfType<DeploymentTargetAnnotation>());

        environment.Resource.Outputs[DomainOutputName] = "salmonisland-e9e6a567.westus3.azurecontainerapps.io";
        environment.Resource.ProvisioningTaskCompletionSource?.TrySetResult();
        var expectedValues = new Dictionary<EndpointProperty, string>
        {
            [EndpointProperty.Url] = "https://api.salmonisland-e9e6a567.westus3.azurecontainerapps.io",
            [EndpointProperty.Host] = "api.salmonisland-e9e6a567.westus3.azurecontainerapps.io",
            [EndpointProperty.IPV4Host] = "api.salmonisland-e9e6a567.westus3.azurecontainerapps.io",
            [EndpointProperty.HostAndPort] = "api.salmonisland-e9e6a567.westus3.azurecontainerapps.io:443",
            [EndpointProperty.Port] = "443",
            [EndpointProperty.TargetPort] = "8080",
            [EndpointProperty.Scheme] = "https",
            [EndpointProperty.TlsEnabled] = "True"
        };
        foreach (var (property, expected) in expectedValues)
        {
            var expression = environment.Resource.GetEndpointPropertyExpression(endpoint.Property(property));
            Assert.Equal(expected, await expression.GetValueAsync(default));
        }

        Assert.False(endpoint.IsAllocated);
        var (_, firstBicep) = await GetManifestWithBicep(target, skipPreparer: true);
        var (_, secondBicep) = await GetManifestWithBicep(target, skipPreparer: true);
        Assert.Equal(firstBicep, secondBicep);
    }

    [Theory]
    [InlineData(EndpointProperty.Port, "443")]
    [InlineData(EndpointProperty.TargetPort, "8080")]
    [InlineData(EndpointProperty.Scheme, "https")]
    [InlineData(EndpointProperty.TlsEnabled, "True")]
    public async Task NonHostPropertiesResolveWithoutMaterializingDeploymentTargets(EndpointProperty property, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var api = builder.AddContainer("api", "myimage").WithHttpEndpoint(targetPort: 8080).WithExternalHttpEndpoints();

        var expression = environment.Resource.GetEndpointPropertyExpression(api.GetEndpoint("http").Property(property));

        Assert.Equal(expected, await expression.GetValueAsync(default));
        Assert.Null(api.Resource.GetDeploymentTargetAnnotation());
    }

    [Theory]
    [InlineData(EndpointProperty.Url)]
    [InlineData(EndpointProperty.Host)]
    [InlineData(EndpointProperty.IPV4Host)]
    [InlineData(EndpointProperty.HostAndPort)]
    [InlineData(EndpointProperty.Port)]
    [InlineData(EndpointProperty.TargetPort)]
    [InlineData(EndpointProperty.Scheme)]
    [InlineData(EndpointProperty.TlsEnabled)]
    public void InternalEndpointReferencesRequireExplicitPublicIngress(EndpointProperty property)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var api = builder.AddContainer("api", "myimage").WithHttpEndpoint(targetPort: 8080);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            environment.Resource.GetEndpointPropertyExpression(api.GetEndpoint("http").Property(property)));

        Assert.Equal(
            "Azure Container Apps Express environment 'env' cannot reference internal endpoint 'http' on resource 'api'. " +
            "Use WithExternalHttpEndpoints() to explicitly enable public HTTPS ingress, or use a standard Azure Container Apps environment.",
            exception.Message);
        Assert.False(api.GetEndpoint("http").EndpointAnnotation.IsExternal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalReferencesIncludingConditionalExpressionsAreRejected(bool conditional)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var api = builder.AddContainer("api", "myimage").WithHttpEndpoint(targetPort: 8080);
        var web = builder.AddContainer("web", "myimage").WithHttpEndpoint(targetPort: 8080);
        if (conditional)
        {
            var enabled = builder.AddParameter("enabled");
            web.WithEnvironment("API", ReferenceExpression.CreateConditional(
                enabled.Resource, bool.TrueString,
                ReferenceExpression.Create($"prefix/{ReferenceExpression.Create($"{api.GetEndpoint("http")}")}"),
                ReferenceExpression.Create($"disabled")));
        }
        else
        {
            web.WithReference(api.GetEndpoint("http"));
        }

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var exception = Assert.Throws<InvalidOperationException>(GetTarget(web.Resource).GetBicepTemplateString);

        Assert.Equal(
            "Azure Container Apps Express environment 'env' cannot reference internal endpoint 'http' on resource 'api'. " +
            "Use WithExternalHttpEndpoints() to explicitly enable public HTTPS ingress, or use a standard Azure Container Apps environment.",
            exception.Message);
        Assert.False(api.GetEndpoint("http").EndpointAnnotation.IsExternal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossEnvironmentReferencesPublishUsingTheProducerDomain(bool expressConsumer)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        var reporter = new TestPipelineActivityReporter(outputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);
        var consumerEnvironment = builder.AddAzureContainerAppEnvironment("consumer");
        if (expressConsumer)
        {
            consumerEnvironment.AsExpress();
        }
        var producerEnvironment = builder.AddAzureContainerAppEnvironment("producer").AsExpress();
        var web = builder.AddContainer("web", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithComputeEnvironment(consumerEnvironment);
        var api = builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(producerEnvironment);
        web.WithReference(api.GetEndpoint("http"));
        web.WithEnvironment("HOST", api.GetEndpoint("http").Property(EndpointProperty.Host));
        web.WithEnvironment("EARLY_HOST", producerEnvironment.Resource.GetHostAddressExpression(api.GetEndpoint("http")));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var webTarget = GetTarget(web.Resource);
        var (manifest, bicep) = await GetManifestWithBicep(webTarget, skipPreparer: true);

        // The hostname resolves through the producing environment's default domain, which is a
        // normal infrastructure module, so standalone publishing can bind it.
        var domain = Assert.Single(webTarget.Parameters.Values.OfType<BicepOutputReference>(),
            output => output.Name == DomainOutputName && output.Resource == producerEnvironment.Resource);
        Assert.Same(producerEnvironment.Resource, domain.Resource);

        await app.RunAsync();

        Assert.NotEqual(CompletionState.CompletedWithError, reporter.ResultCompletionState);
        Assert.True(File.Exists(Path.Combine(workspace.Path, "main.bicep")));
        await Verify(manifest.ToString(), "json").AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task ExpressConsumerUsesPublicStandardEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var standard = builder.AddAzureContainerAppEnvironment("standard");
        var express = builder.AddAzureContainerAppEnvironment("express").AsExpress();
        var api = builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(standard);
        var endpoint = api.GetEndpoint("http");
        var web = builder.AddContainer("web", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithComputeEnvironment(express)
            .WithReference(endpoint)
            .WithEnvironment("API_HOST", endpoint.Property(EndpointProperty.Host))
            .WithEnvironment("API_AUTHORITY", ReferenceExpression.Create($"api={endpoint.Property(EndpointProperty.HostAndPort)}"));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var target = GetTarget(web.Resource);
        var (manifest, bicep) = await GetManifestWithBicep(target, skipPreparer: true);
        var domain = Assert.Single(target.Parameters.Values.OfType<BicepOutputReference>(),
            output => output.Name == DomainOutputName && output.Resource == standard.Resource);
        Assert.Same(standard.Resource, domain.Resource);
        await Verify(manifest.ToString(), "json").AppendContentAsFile(bicep, "bicep");
    }

    [Theory]
    [InlineData(EndpointProperty.Url)]
    [InlineData(EndpointProperty.Host)]
    [InlineData(EndpointProperty.IPV4Host)]
    [InlineData(EndpointProperty.HostAndPort)]
    public async Task ExpressConsumerRejectsPrivateStandardEndpoints(EndpointProperty property)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var standard = builder.AddAzureContainerAppEnvironment("standard");
        var express = builder.AddAzureContainerAppEnvironment("express").AsExpress();
        var api = builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithComputeEnvironment(standard);
        var web = builder.AddContainer("web", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithComputeEnvironment(express);
        if (property is EndpointProperty.Url)
        {
            web.WithReference(api.GetEndpoint("http"));
        }
        else
        {
            web.WithEnvironment("API", ReferenceExpression.Create($"api={api.GetEndpoint("http").Property(property)}"));
        }

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var exception = Assert.Throws<InvalidOperationException>(GetTarget(web.Resource).GetBicepTemplateString);

        Assert.Equal(
            "Azure Container Apps Express environment 'express' cannot reference internal endpoint 'http' on resource 'api'. " +
            "Use WithExternalHttpEndpoints() to explicitly enable public HTTPS ingress, or use a standard Azure Container Apps environment.",
            exception.Message);
        Assert.False(api.GetEndpoint("http").EndpointAnnotation.IsExternal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsumersAcceptPreservedHttpFromStandardEndpoints(bool expressConsumer)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var producer = builder.AddAzureContainerAppEnvironment("producer").WithHttpsUpgrade(false);
        var consumer = builder.AddAzureContainerAppEnvironment("consumer");
        if (expressConsumer)
        {
            consumer.AsExpress();
        }
        var api = builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(producer);
        var web = builder.AddContainer("web", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithComputeEnvironment(consumer)
            .WithReference(api.GetEndpoint("http"))
            .WithEnvironment("API_URL", api.GetEndpoint("http").Property(EndpointProperty.Url));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var target = GetTarget(web.Resource);
        var (manifest, bicep) = await GetManifestWithBicep(target, skipPreparer: true);

        // Express restricts this app's own ingress, not the schemes it calls outbound, so an
        // http:// reference to another environment's endpoint is passed through unchanged.
        Assert.Contains("http://api.${producer_outputs_azure_container_apps_environment_default_domain}", bicep, StringComparison.Ordinal);

        await Verify(manifest.ToString(), "json").AppendContentAsFile(bicep, "bicep");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelfPublicUrlReferencesUseTheEnvironmentDomain(bool environmentExpression)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var api = builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        var endpoint = api.GetEndpoint("http");
        var expression = environmentExpression
            ? environment.Resource.GetEndpointPropertyExpression(endpoint.Property(EndpointProperty.Url))
            : ReferenceExpression.Create($"{endpoint}");
        api.WithEnvironment("SELF_URL", ReferenceExpression.Create($"prefix/{expression}"));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var target = GetTarget(api.Resource);
        var (manifest, bicep) = await GetManifestWithBicep(target, skipPreparer: true);

        // An app can reference its own public URL because the hostname is known before deployment.
        var domain = Assert.Single(target.Parameters.Values.OfType<BicepOutputReference>(), output => output.Name == DomainOutputName);
        Assert.Same(environment.Resource, domain.Resource);

        await Verify(manifest.ToString(), "json").AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task MutualPublicUrlReferencesAreSupported()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var first = builder.AddContainer("first", "myimage").WithHttpEndpoint(targetPort: 8080).WithExternalHttpEndpoints();
        var second = builder.AddContainer("second", "myimage").WithHttpEndpoint(targetPort: 8080).WithExternalHttpEndpoints();
        first.WithReference(second.GetEndpoint("http"));
        second.WithReference(first.GetEndpoint("http"));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var firstTarget = GetTarget(first.Resource);
        var secondTarget = GetTarget(second.Resource);

        // Hostnames are derived from the environment domain, so mutual references introduce no
        // deployment-order dependency between the two apps.
        foreach (var target in new[] { firstTarget, secondTarget })
        {
            Assert.DoesNotContain(target.Parameters.Values.OfType<BicepOutputReference>(),
                output => output.Resource == firstTarget || output.Resource == secondTarget);
        }

        Assert.Empty(firstTarget.GetAzureReferences().OfType<AzureContainerAppResource>());
        Assert.Empty(secondTarget.GetAzureReferences().OfType<AzureContainerAppResource>());
    }

    [Fact]
    public async Task DeploymentSummaryUsesEnvironmentDomainWithoutDashboard()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var api = builder.AddContainer("api", "myimage").WithHttpEndpoint(targetPort: 8080).WithExternalHttpEndpoints();
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var target = GetTarget(api.Resource);
        environment.Resource.Outputs[DomainOutputName] = "salmonisland-e9e6a567.westus3.azurecontainerapps.io";
        environment.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"] =
            "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/example-rg/providers/Microsoft.App/managedEnvironments/env";
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(), builder.ExecutionContext,
            app.Services, NullLogger.Instance, TestContext.Current.CancellationToken);
        var steps = new List<PipelineStep>();
        foreach (var annotation in environment.Resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            steps.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = context,
                Resource = environment.Resource
            }));
        }
        target.ProvisioningTaskCompletionSource?.TrySetResult();
        environment.Resource.ProvisioningTaskCompletionSource?.TrySetResult();
        var summaryStep = Assert.Single(steps, step => step.Tags.Contains("print-summary"));
        Assert.Equal("print-api-summary", summaryStep.Name);
        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("summary");
        await summaryStep.Action(new PipelineStepContext { PipelineContext = context, ReportingStep = reportingStep });

        await Verify(context.Summary.Items);
    }

    private static AzureContainerAppResource GetTarget(IResource resource) =>
        Assert.IsType<AzureContainerAppResource>(resource.GetDeploymentTargetAnnotation()?.DeploymentTarget);
}
