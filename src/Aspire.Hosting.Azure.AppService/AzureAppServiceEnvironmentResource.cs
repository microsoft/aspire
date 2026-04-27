// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppService;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.AppService;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure App Service Environment resource.
/// </summary>
#pragma warning disable CS0618 // Type or member is obsolete
public class AzureAppServiceEnvironmentResource :
    AzureProvisioningResource,
    IAzureComputeEnvironmentResource,
    IAzureContainerRegistry
#pragma warning restore CS0618 // Type or member is obsolete
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureAppServiceEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the Azure App Service Environment.</param>
    /// <param name="configureInfrastructure">The callback to configure the Azure infrastructure for this resource.</param>
    public AzureAppServiceEnvironmentResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
        : base(name, configureInfrastructure)
    {
        // Add pipeline step annotation to create steps and expand deployment target steps
        Annotations.Add(new PipelineStepAnnotation(async (factoryContext) =>
        {
            var model = factoryContext.PipelineContext.Model;
            var steps = new List<PipelineStep>();

            // Add prepare-azure-app-service-{name} step that materializes deployment targets
            // for compute resources targeted to this environment. Runs during BeforeStart so that
            // BeforeStartEvent subscribers (and downstream code) can observe the deployment targets.
            var prepareStep = new PipelineStep
            {
                Name = $"prepare-azure-app-service-{name}",
                Description = $"Prepares Azure App Service deployment targets for {name}.",
                Action = ctx => PrepareDeploymentTargetsAsync(ctx),
                DependsOnSteps = [AzureEnvironmentResource.PrepareResourcesStepName],
                RequiredBySteps = [WellKnownPipelineSteps.BeforeStart]
            };

            steps.Add(prepareStep);

            // Add validation step that checks for environment variable name issues
            // This runs early and provides user-friendly error messages through the activity reporter
            var validateStep = new PipelineStep
            {
                Name = $"validate-appservice-config-{name}",
                Description = $"Validates Azure App Service configuration for {name}.",
                Action = ctx => ValidateAppServiceConfigurationAsync(ctx, model),
                RequiredBySteps = [WellKnownPipelineSteps.Publish],
                DependsOnSteps = [WellKnownPipelineSteps.PublishPrereq]
            };

            steps.Add(validateStep);

            // Add print-dashboard-url step
            var printDashboardUrlStep = new PipelineStep
            {
                Name = $"print-dashboard-url-{name}",
                Description = $"Prints the deployment summary and dashboard URL for {name}.",
                Action = ctx => PrintDashboardUrlAsync(ctx),
                Tags = ["print-summary"],
                DependsOnSteps = [AzureEnvironmentResource.ProvisionInfrastructureStepName],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy]
            };

            steps.Add(printDashboardUrlStep);

            // Expand deployment target steps for all compute resources
            // This ensures the push/provision steps from deployment targets are included in the pipeline
            foreach (var computeResource in model.GetComputeResources())
            {
                var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(this)?.DeploymentTarget;

                if (deploymentTarget != null && deploymentTarget.TryGetAnnotationsOfType<PipelineStepAnnotation>(out var annotations))
                {
                    // Resolve the deployment target's PipelineStepAnnotation and expand its steps
                    // We do this because the deployment target is not in the model
                    foreach (var annotation in annotations)
                    {
                        var childFactoryContext = new PipelineStepFactoryContext
                        {
                            PipelineContext = factoryContext.PipelineContext,
                            Resource = deploymentTarget
                        };

                        var deploymentTargetSteps = await annotation.CreateStepsAsync(childFactoryContext).ConfigureAwait(false);

                        foreach (var step in deploymentTargetSteps)
                        {
                            // Ensure the step is associated with the deployment target resource
                            step.Resource ??= deploymentTarget;
                        }

                        steps.AddRange(deploymentTargetSteps);
                    }
                }
            }

            return steps;
        }));

        // Add pipeline configuration annotation to wire up dependencies
        // This is where we wire up the build steps created by the resources
        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            // Wire up build step dependencies
            // Build steps are created by ProjectResource and ContainerResource
            foreach (var computeResource in context.Model.GetComputeResources())
            {
                var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(this)?.DeploymentTarget;

                if (deploymentTarget is null)
                {
                    continue;
                }

                // Execute the PipelineConfigurationAnnotation callbacks on the deployment target
                if (deploymentTarget.TryGetAnnotationsOfType<PipelineConfigurationAnnotation>(out var annotations))
                {
                    foreach (var annotation in annotations)
                    {
                        annotation.Callback(context);
                    }
                }
            }

            // This ensures that resources that have to be built before deployments are handled
            foreach (var computeResource in context.Model.GetBuildResources())
            {
                context.GetSteps(computeResource, WellKnownPipelineTags.BuildCompute)
                        .RequiredBy(WellKnownPipelineSteps.Deploy)
                        .DependsOn(WellKnownPipelineSteps.DeployPrereq);
            }

            // Make print-summary step depend on provisioning of this environment
            var printSummarySteps = context.GetSteps(this, "print-summary");
            var provisionSteps = context.GetSteps(this, WellKnownPipelineTags.ProvisionInfrastructure);
            printSummarySteps.DependsOn(provisionSteps);
        }));
    }

    /// <summary>
    /// Materializes Azure App Service deployment targets for compute resources targeted to this
    /// environment. Invoked by the per-environment <c>prepare-azure-app-service-{name}</c>
    /// pipeline step, which runs after <c>azure-prepare-resources</c> so role-assignment resources
    /// are present in the model and can be referenced by the generated
    /// <see cref="DeploymentTargetAnnotation"/> instances.
    /// </summary>
    private async Task PrepareDeploymentTargetsAsync(PipelineStepContext context)
    {
        var appModel = context.Model;
        var executionContext = context.ExecutionContext;
        var services = context.Services;
        var cancellationToken = context.CancellationToken;

        if (!executionContext.IsPublishMode)
        {
            return;
        }

        // Remove the default container registry from the model if an explicit registry is configured
        if (this.HasAnnotationOfType<ContainerRegistryReferenceAnnotation>() &&
            DefaultContainerRegistry is not null)
        {
            appModel.Resources.Remove(DefaultContainerRegistry);
        }

        var logger = services.GetRequiredService<ILogger<AzureAppServiceEnvironmentResource>>();
        var provisioningOptions = services.GetRequiredService<IOptions<AzureProvisioningOptions>>();

        var appServiceEnvironmentContext = new AzureAppServiceEnvironmentContext(
            logger,
            executionContext,
            this,
            services);

        // Annotate the environment with its context
        Annotations.Add(new AzureAppServiceEnvironmentContextAnnotation(appServiceEnvironmentContext));

        foreach (var resource in appModel.GetComputeResources())
        {
            // Support project resources and containers with Dockerfile
            if (resource is not ProjectResource && !(resource.IsContainer() && resource.TryGetAnnotationsOfType<DockerfileBuildAnnotation>(out _)))
            {
                continue;
            }

            // Skip resources that are explicitly targeted to a different compute environment
            var resourceComputeEnvironment = resource.GetComputeEnvironment();
            if (resourceComputeEnvironment is not null && resourceComputeEnvironment != this)
            {
                continue;
            }

            var website = await appServiceEnvironmentContext.CreateAppServiceAsync(resource, provisioningOptions.Value, cancellationToken).ConfigureAwait(false);

            resource.Annotations.Add(new DeploymentTargetAnnotation(website)
            {
                ContainerRegistry = this,
                ComputeEnvironment = this
            });
        }

        // Log once about all HTTP endpoints upgraded to HTTPS
        appServiceEnvironmentContext.LogHttpsUpgradeIfNeeded();
    }

    private async Task PrintDashboardUrlAsync(PipelineStepContext context)
    {
        var dashboardUri = await DashboardUriReference.GetValueAsync(context.CancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(dashboardUri))
        {
            context.Summary.Add("📊 Dashboard", new MarkdownString($"[{dashboardUri}]({dashboardUri})"));
        }

        await context.ReportingStep.CompleteAsync(
            new MarkdownString($"Dashboard available at [{dashboardUri}]({dashboardUri})"),
            CompletionState.Completed,
            context.CancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateAppServiceConfigurationAsync(PipelineStepContext context, DistributedApplicationModel model)
    {
        // Check for validation errors in all app service contexts
        if (!this.TryGetLastAnnotation<AzureAppServiceEnvironmentContextAnnotation>(out var contextAnnotation))
        {
            await context.ReportingStep.CompleteAsync(
                "App Service configuration validated",
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
            return;
        }

        var errors = new List<string>();

        foreach (var computeResource in model.GetComputeResources())
        {
            var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(this)?.DeploymentTarget;
            if (deploymentTarget is null)
            {
                continue;
            }

            // Get the app service context for this resource and validate
            try
            {
                var appServiceContext = contextAnnotation.EnvironmentContext.GetAppServiceContext(computeResource);
                var validationError = ValidateEnvironmentVariableNames(computeResource, appServiceContext);
                if (validationError is not null)
                {
                    errors.Add(validationError);
                }
            }
            catch (InvalidOperationException)
            {
                // Context not found for this resource - skip
            }
        }

        if (errors.Count > 0)
        {
            // Report each error through the activity reporter for user-friendly display
            foreach (var error in errors)
            {
                context.ReportingStep.Log(LogLevel.Error, error);
            }

            await context.ReportingStep.CompleteAsync(
                "App Service configuration validation failed",
                CompletionState.CompletedWithError,
                context.CancellationToken).ConfigureAwait(false);

            // Throw to stop the pipeline - the error has already been reported through the activity reporter
            throw new DistributedApplicationException("App Service configuration validation failed. See errors above.");
        }

        await context.ReportingStep.CompleteAsync(
            "App Service configuration validated",
            CompletionState.Completed,
            context.CancellationToken).ConfigureAwait(false);
    }

    private static string? ValidateEnvironmentVariableNames(IResource resource, AzureAppServiceWebsiteContext appServiceContext)
    {
        if (resource.HasAnnotationOfType<AzureAppServiceIgnoreEnvironmentVariableChecksAnnotation>())
        {
            return null;
        }

        // Azure App Service removes '-' from environment variable names at runtime.
        // This breaks configuration binding for connection strings that use dashed names.
        var problematicKeys = appServiceContext.EnvironmentVariables.Keys
            .Where(static k => k.Contains('-', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (problematicKeys.Length == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Resource '" + resource.Name + "' cannot be published to Azure App Service because it has environment variables with '-' in the name.");
        sb.AppendLine();
        sb.AppendLine("Azure App Service removes '-' characters from environment variable names at runtime.");
        sb.AppendLine("This will change the keys and can prevent Aspire client integrations from finding the expected connection string.");
        sb.AppendLine();
        sb.AppendLine("Affected setting(s):");

        foreach (var key in problematicKeys)
        {
            var normalized = key.Replace("-", string.Empty, StringComparison.Ordinal);
            sb.AppendLine("  - " + key + " (becomes " + normalized + ")");
        }

        sb.AppendLine();
        sb.AppendLine("Fix options:");
        sb.AppendLine("  - Use a dash-free connection name in the AppHost (e.g. WithReference(resource, connectionName: \"mydb\")).");
        sb.AppendLine($"  - Call {nameof(AzureAppServiceComputeResourceExtensions.SkipEnvironmentVariableNameChecks)}() on this resource to bypass this validation.");

        return sb.ToString();
    }

    // We don't want these to be public if we end up with an app service
    // per compute resource.
    internal BicepOutputReference PlanIdOutputReference => new("planId", this);
    internal BicepOutputReference ContainerRegistryUrl => new("AZURE_CONTAINER_REGISTRY_ENDPOINT", this);
    internal BicepOutputReference ContainerRegistryName => new("AZURE_CONTAINER_REGISTRY_NAME", this);
    internal BicepOutputReference ContainerRegistryManagedIdentityId => new("AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID", this);
    internal BicepOutputReference ContainerRegistryClientId => new("AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID", this);
    internal BicepOutputReference WebsiteContributorManagedIdentityId => new("AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_ID", this);
    internal BicepOutputReference WebsiteContributorManagedIdentityPrincipalId => new("AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_PRINCIPAL_ID", this);

    /// <summary>
    /// Gets the suffix added to each web app created in this App Service Environment.
    /// </summary>
    internal BicepOutputReference WebSiteSuffix => new("webSiteSuffix", this);

    /// <summary>
    /// When true, HTTP endpoints are not upgraded to HTTPS. Default is false (HTTP→HTTPS upgrade is enabled).
    /// </summary>
    internal bool PreserveHttpEndpoints { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Aspire dashboard should be included in the container app environment.
    /// Default is true.
    /// </summary>
    internal bool EnableDashboard { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Application Insights telemetry should be enabled in the app service environment.
    /// </summary>
    internal bool EnableApplicationInsights { get; set; }

    /// <summary>
    /// Gets the location for the Application Insights resource. If <c>null</c>, the resource group location is used.
    /// </summary>
    internal string? ApplicationInsightsLocation { get; set; }

    /// <summary>
    /// Parameter resource for the Application Insights location.
    /// </summary>
    internal ParameterResource? ApplicationInsightsLocationParameter { get; set; }

    /// <summary>
    /// Application Insights resource.
    /// </summary>
    internal AzureApplicationInsightsResource? ApplicationInsightsResource { get; set; }

    /// <summary>
    /// Deployment slot parameter resource for the App Service Environment.
    /// </summary>
    internal ParameterResource? DeploymentSlotParameter { get; set; }

    /// <summary>
    /// Deployment slot for the App Service Environment.
    /// </summary>
    internal string? DeploymentSlot { get; set; }

    /// <summary>
    /// Gets the name of the App Service Plan.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the URI of the App Service Environment dashboard.
    /// </summary>
    public BicepOutputReference DashboardUriReference => new("AZURE_APP_SERVICE_DASHBOARD_URI", this);

    /// <summary>
    /// Gets the Application Insights Instrumentation Key.
    /// </summary>
    public BicepOutputReference AzureAppInsightsInstrumentationKeyReference =>
        new("AZURE_APPLICATION_INSIGHTS_INSTRUMENTATIONKEY", this);

    /// <summary>
    /// Gets the Application Insights Connection String.
    /// </summary>
    public BicepOutputReference AzureAppInsightsConnectionStringReference =>
        new("AZURE_APPLICATION_INSIGHTS_CONNECTION_STRING", this);

    internal static BicepValue<string> GetWebSiteSuffixBicep() =>
        BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id);

    /// <summary>
    /// Gets the default container registry for this environment.
    /// </summary>
    internal AzureContainerRegistryResource? DefaultContainerRegistry { get; set; }

    ReferenceExpression IContainerRegistry.Name => GetContainerRegistry()?.Name ?? ReferenceExpression.Create($"{ContainerRegistryName}");

    ReferenceExpression IContainerRegistry.Endpoint => GetContainerRegistry()?.Endpoint ?? ReferenceExpression.Create($"{ContainerRegistryUrl}");

    IAzureContainerRegistryResource? IAzureComputeEnvironmentResource.ContainerRegistry => ContainerRegistry;

    private IContainerRegistry? GetContainerRegistry()
    {
        // Check for explicit container registry reference annotation
        if (this.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var annotation))
        {
            return annotation.Registry;
        }

        // Fall back to default container registry
        return DefaultContainerRegistry;
    }

    /// <summary>
    /// Gets the Azure Container Registry resource used by this Azure App Service Environment resource.
    /// </summary>
    public AzureContainerRegistryResource? ContainerRegistry
    {
        get
        {
            var registry = GetContainerRegistry();

            if (registry is null)
            {
                return null;
            }

            if (registry is not AzureContainerRegistryResource azureRegistry)
            {
                throw new InvalidOperationException(
                    $"The container registry configured for the Azure App Service Environment '{Name}' is not an Azure Container Registry. " +
                    $"Only Azure Container Registry resources are supported. Use '.WithAzureContainerRegistry()' to configure an Azure Container Registry.");
            }

            return azureRegistry;
        }
    }

#pragma warning disable CS0618 // Type or member is obsolete
    ReferenceExpression IAzureContainerRegistry.ManagedIdentityId => ReferenceExpression.Create($"{ContainerRegistryManagedIdentityId}");
#pragma warning restore CS0618 // Type or member is obsolete

    /// <inheritdoc/>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        var resource = endpointReference.Resource;
        return ReferenceExpression.Create($"{resource.Name.ToLowerInvariant()}-{WebSiteSuffix}.azurewebsites.net");
    }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if an AppServicePlan with the same identifier already exists
        var existingPlan = resources.OfType<AppServicePlan>().SingleOrDefault(plan => plan.BicepIdentifier == bicepIdentifier);

        if (existingPlan is not null)
        {
            return existingPlan;
        }

        // Create and add new resource if it doesn't exist
        var plan = AppServicePlan.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            plan))
        {
            plan.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(plan);
        return plan;
    }
}
