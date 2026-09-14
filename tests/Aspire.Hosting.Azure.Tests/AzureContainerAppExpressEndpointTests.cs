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
    private const string FqdnOutputName = "AZURE_CONTAINER_APP_INGRESS_FQDN";

    [Fact]
    public async Task PublicReferencesUseIngressFqdnAndPreserveSelfTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env").AsExpress();
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
        var reference = Assert.Single(webTarget.Parameters.Values.OfType<BicepOutputReference>(), output => output.Name == FqdnOutputName);
        Assert.Same(apiTarget, reference.Resource);
        Assert.False(endpoint.IsAllocated);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep")
            .AppendContentAsFile(apiBicep, "bicep", "api");
    }

    [Fact]
    public async Task EndpointExpressionsCreatedBeforePreparationUseTheFinalTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var api = builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        var endpoint = api.GetEndpoint("http");
        var host = environment.Resource.GetHostAddressExpression(endpoint);
        var output = Assert.IsType<BicepOutputReference>(Assert.Single(host.ValueProviders));
        Assert.Equal(FqdnOutputName, output.Name);
        Assert.Null(api.Resource.GetDeploymentTargetAnnotation());

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var target = GetTarget(api.Resource);
        Assert.Same(target, output.Resource);
        await ExecuteBeforeStartHooksAsync(app, default);
        Assert.Same(target, GetTarget(api.Resource));
        Assert.Single(api.Resource.Annotations.OfType<DeploymentTargetAnnotation>());

        target.Outputs[FqdnOutputName] = "provider-assigned.example";
        target.ProvisioningTaskCompletionSource?.TrySetResult();
        var expectedValues = new Dictionary<EndpointProperty, string>
        {
            [EndpointProperty.Url] = "https://provider-assigned.example",
            [EndpointProperty.Host] = "provider-assigned.example",
            [EndpointProperty.IPV4Host] = "provider-assigned.example",
            [EndpointProperty.HostAndPort] = "provider-assigned.example:443",
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
    public async Task NonHostPropertiesDoNotRequirePublicIngressOrDeploymentOutputs(EndpointProperty property, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var api = builder.AddContainer("api", "myimage").WithHttpEndpoint(targetPort: 8080);

        var expression = environment.Resource.GetEndpointPropertyExpression(api.GetEndpoint("http").Property(property));

        Assert.Equal(expected, await expression.GetValueAsync(default));
        Assert.Null(api.Resource.GetDeploymentTargetAnnotation());
    }

    [Theory]
    [InlineData(EndpointProperty.Url)]
    [InlineData(EndpointProperty.Host)]
    [InlineData(EndpointProperty.IPV4Host)]
    [InlineData(EndpointProperty.HostAndPort)]
    public void InternalHostPropertiesRequireExplicitPublicIngress(EndpointProperty property)
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
    public async Task CrossEnvironmentReferencesRequireNativeDeployment(bool expressConsumer)
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

        var apiTarget = GetTarget(api.Resource);
        var webTarget = GetTarget(web.Resource);
        var (manifest, bicep) = await GetManifestWithBicep(webTarget, skipPreparer: true);
        var reference = Assert.Single(webTarget.Parameters.Values.OfType<BicepOutputReference>(),
            output => output.Name == FqdnOutputName);
        Assert.Same(apiTarget, reference.Resource);

        await app.RunAsync();

        const string expectedError =
            "Publishing output 'AZURE_CONTAINER_APP_INGRESS_FQDN' from standalone deployment target 'api-containerapp' is not supported. " +
            "The generated infrastructure template cannot bind outputs between separately deployed applications. " +
            "Use native 'aspire deploy' to resolve these dependencies, or remove the application-output reference before publishing.";
        Assert.Equal(CompletionState.CompletedWithError, reporter.ResultCompletionState);
        Assert.Contains(reporter.CompletedTasks, task =>
            task.TaskStatusText == "Writing Azure Bicep templates" &&
            task.CompletionState == CompletionState.CompletedWithError &&
            task.CompletionMessage == $"Failed to write Azure Bicep templates: {expectedError}");
        Assert.False(File.Exists(Path.Combine(workspace.Path, "main.bicep")));
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
            output => output.Name == "AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN");
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
    public async Task OnlyExpressConsumersRejectPreservedHttpFromStandardEndpoints(bool expressConsumer)
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
        if (expressConsumer)
        {
            var exception = Assert.Throws<InvalidOperationException>(target.GetBicepTemplateString);
            Assert.Equal(
                "Azure Container Apps Express environment 'consumer' cannot reference endpoint 'http' on resource 'api' using HTTP. " +
                "Environment 'producer' preserves HTTP endpoints. Enable HTTPS upgrade with WithHttpsUpgrade(true), " +
                "reference an HTTPS endpoint, or use a standard Azure Container Apps environment.",
                exception.Message);
        }
        else
        {
            var (manifest, bicep) = await GetManifestWithBicep(target, skipPreparer: true);
            await Verify(manifest.ToString(), "json").AppendContentAsFile(bicep, "bicep");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelfPublicUrlDependenciesHaveExpressGuidance(bool environmentExpression)
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
        var exception = Assert.Throws<InvalidOperationException>(GetTarget(api.Resource).GetBicepTemplateString);

        Assert.Equal(
            "Resource 'api' in Azure Container Apps Express environment 'env' cannot use its own public endpoint URL or hostname during deployment. " +
            "Express assigns the ingress FQDN after the app is deployed. Remove the self-reference, use TargetPort for the application's listening port, " +
            "or use a standard Azure Container Apps environment. Public endpoint references must form an acyclic deployment dependency graph.",
            exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputDependenciesUsePipelineCycleValidationBeforeProvisioning(bool circular)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var first = builder.AddContainer("first", "myimage").WithHttpEndpoint(targetPort: 8080).WithExternalHttpEndpoints();
        var second = builder.AddContainer("second", "myimage").WithHttpEndpoint(targetPort: 8080).WithExternalHttpEndpoints();
        first.WithReference(second.GetEndpoint("http"));
        if (circular)
        {
            second.WithReference(first.GetEndpoint("http"));
        }

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var targets = new[] { GetTarget(first.Resource), GetTarget(second.Resource) };
        var provisioned = 0;
        var steps = targets.Select(target => new PipelineStep
        {
            Name = $"provision-{target.Name}",
            Resource = target,
            Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
            RequiredBySteps = [WellKnownPipelineSteps.Deploy],
            Action = _ =>
            {
                Interlocked.Increment(ref provisioned);
                return Task.CompletedTask;
            }
        }).ToList();
        var configuration = new PipelineConfigurationContext
        {
            Model = app.Services.GetRequiredService<DistributedApplicationModel>(),
            Services = app.Services,
            Steps = steps
        };
        foreach (var target in targets)
        {
            foreach (var annotation in target.Annotations.OfType<PipelineConfigurationAnnotation>())
            {
                await annotation.Callback(configuration);
            }
        }

        Assert.Equal(["provision-second-containerapp"], steps[0].DependsOnSteps);
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();
        pipeline.AddStep(AzureEnvironmentResource.PrepareResourcesStepName, static _ => Task.CompletedTask);
        foreach (var step in steps)
        {
            pipeline.AddStep(step);
        }
        // Execute the real dependency graph with inert provisioning actions and no Azure resources
        // in the execution model. Even a regression in cycle validation cannot call a provider.
        var context = new PipelineContext(new DistributedApplicationModel([]), builder.ExecutionContext,
            app.Services, NullLogger.Instance, TestContext.Current.CancellationToken);
        if (circular)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context));
            Assert.Contains("Circular dependency detected in pipeline steps:", exception.Message);
            Assert.Contains("provision-first-containerapp", exception.Message);
            Assert.Contains("provision-second-containerapp", exception.Message);
            Assert.Equal(0, provisioned);
        }
        else
        {
            await pipeline.ExecuteAsync(context);
            Assert.Equal(2, provisioned);
        }
    }

    [Fact]
    public async Task DeploymentSummaryUsesActualFqdnWithoutEnvironmentDomainOrDashboard()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var api = builder.AddContainer("api", "myimage").WithHttpEndpoint(targetPort: 8080).WithExternalHttpEndpoints();
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var target = GetTarget(api.Resource);
        target.Outputs[FqdnOutputName] = "provider-assigned.example";
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
