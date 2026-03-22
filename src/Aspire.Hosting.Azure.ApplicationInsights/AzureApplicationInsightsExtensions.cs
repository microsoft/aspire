// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.ApplicationInsights;
using Azure.Provisioning.OperationalInsights;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding the Azure ApplicationInsights resources to the application model.
/// </summary>
public static class AzureApplicationInsightsExtensions
{
    /// <summary>
    /// Adds an Azure Application Insights resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureApplicationInsightsResource}"/>.</returns>
    [AspireExport("addAzureApplicationInsights", Description = "Adds an Azure Application Insights resource")]
    public static IResourceBuilder<AzureApplicationInsightsResource> AddAzureApplicationInsights(this IDistributedApplicationBuilder builder, [ResourceName] string name)
        => AddAzureApplicationInsights(builder, name, logAnalyticsWorkspace: null);

    /// <summary>
    /// Adds an Azure Application Insights resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="logAnalyticsWorkspace">A resource builder for the log analytics workspace.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureApplicationInsightsResource}"/>.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use <see cref="AddAzureApplicationInsights(IDistributedApplicationBuilder, string)"/> and <see cref="WithLogAnalyticsWorkspace(IResourceBuilder{AzureApplicationInsightsResource}, IResourceBuilder{AzureLogAnalyticsWorkspaceResource})"/> instead.</remarks>
    [AspireExportIgnore(Reason = "logAnalyticsWorkspace parameter cannot be made optional in ATS. Use the single-parameter overload with WithLogAnalyticsWorkspace instead.")]
    public static IResourceBuilder<AzureApplicationInsightsResource> AddAzureApplicationInsights(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<AzureLogAnalyticsWorkspaceResource>? logAnalyticsWorkspace)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var configureInfrastructure = (AzureResourceInfrastructure infrastructure) =>
        {
            var azureResource = (AzureApplicationInsightsResource)infrastructure.AspireResource;

            var appInsights = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = ApplicationInsightsComponent.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                (infrastructure) =>
                {
                    var appTypeParameter = new ProvisioningParameter("applicationType", typeof(string))
                    {
                        Value = "web"
                    };
                    infrastructure.Add(appTypeParameter);

                    var kindParameter = new ProvisioningParameter("kind", typeof(string))
                    {
                        Value = "web"
                    };
                    infrastructure.Add(kindParameter);

                    var appInsights = new ApplicationInsightsComponent(infrastructure.AspireResource.GetBicepIdentifier())
                    {
                        ApplicationType = appTypeParameter,
                        Kind = kindParameter,
                        Tags = { { "aspire-resource-name", name } }
                    };

                    // Check for LogAnalyticsWorkspaceReferenceAnnotation on the AzureApplicationInsightsResource
                    if (azureResource.TryGetLastAnnotation<LogAnalyticsWorkspaceReferenceAnnotation>(out var annotation))
                    {
                        appInsights.WorkspaceResourceId = annotation.WorkspaceId.AsProvisioningParameter(infrastructure);
                    }
                    else
                    {
                        // ... otherwise create one ourselves.
                        var autoInjectedLogAnalyticsWorkspaceName = $"law_{appInsights.BicepIdentifier}";
                        var autoInjectedLogAnalyticsWorkspace = new OperationalInsightsWorkspace(autoInjectedLogAnalyticsWorkspaceName)
                        {
                            Sku = new OperationalInsightsWorkspaceSku()
                            {
                                Name = OperationalInsightsWorkspaceSkuName.PerGB2018
                            },
                            Tags = { { "aspire-resource-name", autoInjectedLogAnalyticsWorkspaceName } }
                        };
                        infrastructure.Add(autoInjectedLogAnalyticsWorkspace);

                        // If the user does not supply a log analytics workspace of their own we still create a parameter on the Aspire
                        // side and the CDK side so that AZD can fill the value in with the one it generates.
                        appInsights.WorkspaceResourceId = autoInjectedLogAnalyticsWorkspace.Id;
                    }

                    return appInsights;
                });

            infrastructure.Add(new ProvisioningOutput("appInsightsConnectionString", typeof(string)) { Value = appInsights.ConnectionString.ToBicepExpression() });

            // Add name output for the resource to externalize role assignments
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = appInsights.Name.ToBicepExpression() });

            // Add id output for the resource for AI Foundry connections
            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = appInsights.Id.ToBicepExpression() });
        };

        var resource = new AzureApplicationInsightsResource(name, configureInfrastructure);

        var rb = builder.AddResource(resource);

        if (logAnalyticsWorkspace != null)
        {
            // If a Log Analytics Workspace resource is provided use it directly.
            rb.WithLogAnalyticsWorkspace(logAnalyticsWorkspace);
        }

        return rb;
    }

    /// <summary>
    /// Configures the Application Insights resource to use an existing Log Analytics Workspace via a <see cref="BicepOutputReference"/>.
    /// </summary>
    /// <param name="builder">The resource builder for <see cref="AzureApplicationInsightsResource"/>.</param>
    /// <param name="workspaceId">The <see cref="BicepOutputReference"/> for the Log Analytics Workspace resource id.</param>
    /// <returns>The <see cref="IResourceBuilder{AzureApplicationInsightsResource}"/> for chaining.</returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the overload that accepts an <see cref="IResourceBuilder{AzureLogAnalyticsWorkspaceResource}"/> instead.</remarks>
    [AspireExportIgnore(Reason = "BicepOutputReference is not ATS-compatible. Use the IResourceBuilder<AzureLogAnalyticsWorkspaceResource> overload instead.")]
    public static IResourceBuilder<AzureApplicationInsightsResource> WithLogAnalyticsWorkspace(
        this IResourceBuilder<AzureApplicationInsightsResource> builder,
        BicepOutputReference workspaceId)
    {
        return builder.WithAnnotation(new LogAnalyticsWorkspaceReferenceAnnotation(workspaceId));
    }

    /// <summary>
    /// Configures the Application Insights resource to use the specified Log Analytics Workspace resource.
    /// </summary>
    /// <param name="builder">The resource builder for <see cref="AzureApplicationInsightsResource"/>.</param>
    /// <param name="logAnalyticsWorkspace">The resource builder for the <see cref="AzureLogAnalyticsWorkspaceResource"/>.</param>
    /// <returns>The <see cref="IResourceBuilder{AzureApplicationInsightsResource}"/> for chaining.</returns>
    [AspireExport("withLogAnalyticsWorkspace", Description = "Configures the Application Insights resource to use a Log Analytics Workspace")]
    public static IResourceBuilder<AzureApplicationInsightsResource> WithLogAnalyticsWorkspace(
        this IResourceBuilder<AzureApplicationInsightsResource> builder,
        IResourceBuilder<AzureLogAnalyticsWorkspaceResource> logAnalyticsWorkspace)
    {
        return builder.WithLogAnalyticsWorkspace(logAnalyticsWorkspace.Resource.WorkspaceId);
    }

    // REVIEW: This isn't strongly typed, but it allows us to pass the workspaceId as a BicepOutputReference
    private sealed class LogAnalyticsWorkspaceReferenceAnnotation(BicepOutputReference workspaceId) : IResourceAnnotation
    {
        public BicepOutputReference WorkspaceId { get; } = workspaceId;
    }
}
