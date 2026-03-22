// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for customizing Azure Container App resource.
/// </summary>
public static class ContainerAppExtensions
{
    /// <summary>
    /// Configures the custom domain for the container app.
    /// </summary>
    /// <param name="app">The container app resource to configure for custom domain usage.</param>
    /// <param name="customDomain">A resource builder for a parameter resource capturing the name of the custom domain.</param>
    /// <param name="certificateName">A resource builder for a parameter resource capturing the name of the certificate configured in the Azure Portal.</param>
    /// <exception cref="ArgumentException">Throws if the container app resource is not parented to a <see cref="AzureResourceInfrastructure"/>.</exception>
    /// <remarks>
    /// <para>The <see cref="ConfigureCustomDomain(ContainerApp, IResourceBuilder{ParameterResource}, IResourceBuilder{ParameterResource})"/> extension method
    /// simplifies the process of assigning a custom domain to a container app resource when it is deployed. It has no impact on local development.</para>
    /// <para>The <see cref="ConfigureCustomDomain(ContainerApp, IResourceBuilder{ParameterResource}, IResourceBuilder{ParameterResource})"/> method is used
    /// in conjunction with the <see cref="AzureContainerAppContainerExtensions.PublishAsAzureContainerApp{T}(IResourceBuilder{T}, Action{AzureResourceInfrastructure, ContainerApp})"/>
    /// callback. Assigning a custom domain to a container app resource is a multi-step process and requires multiple deployments.</para>
    /// <para>The <see cref="ConfigureCustomDomain(ContainerApp, IResourceBuilder{ParameterResource}, IResourceBuilder{ParameterResource})"/> method takes
    /// two arguments which are parameter resource builders. The first is a parameter that represents the custom domain and the second is a parameter that
    /// represents the name of the managed certificate provisioned via the Azure Portal</para>
    /// <para>When deploying with custom domains configured for the first time leave the <paramref name="certificateName"/> parameter empty (when prompted
    /// by the Azure Developer CLI). Once the application is deployed successfully access to the Azure Portal to bind the custom domain to a managed SSL
    /// certificate. Once the certificate is successfully provisioned, subsequent deployments of the application can use this certificate name when the
    /// <paramref name="certificateName"/> is prompted.</para>
    /// <para>For deployments triggered locally by the Azure Developer CLI the <c>config.json</c> file in the <c>.azure/{environment name}</c> path
    /// can by modified with the certificate name since Azure Developer CLI will not prompt again for the value.</para>
    /// <example>
    /// This example shows declaring two parameters to capture the custom domain and certificate name and
    /// passing them to the <see cref="ConfigureCustomDomain(ContainerApp, IResourceBuilder{ParameterResource}, IResourceBuilder{ParameterResource})"/>
    /// method via the <see cref="AzureContainerAppContainerExtensions.PublishAsAzureContainerApp{T}(IResourceBuilder{T}, Action{AzureResourceInfrastructure, ContainerApp})"/>
    /// extension method.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder();
    /// var customDomain = builder.AddParameter("customDomain"); // Value provided at first deployment.
    /// var certificateName = builder.AddParameter("certificateName"); // Value provided at second and subsequent deployments.
    /// builder.AddProject&lt;Projects.InventoryService&gt;("inventory")
    ///        .PublishAsAzureContainerApp((module, app) =>
    ///        {
    ///          app.ConfigureCustomDomain(customDomain, certificateName);
    ///        });
    /// </code>
    /// </example>
    /// <para>This method is not available in polyglot app hosts.</para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Extends ContainerApp (Azure.Provisioning type) which is not an IResourceBuilder<T> target, so the ATS codegen cannot generate a wrapper class for it.")]
    [Experimental("ASPIREACADOMAINS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static void ConfigureCustomDomain(this ContainerApp app, IResourceBuilder<ParameterResource> customDomain, IResourceBuilder<ParameterResource> certificateName)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(customDomain);
        ArgumentNullException.ThrowIfNull(certificateName);

        if (app.ParentInfrastructure is not AzureResourceInfrastructure module)
        {
            throw new ArgumentException("Cannot configure custom domain when resource is not parented by ResourceModuleConstruct.", nameof(app));
        }

        var containerAppManagedEnvironmentId = app.EnvironmentId;
        var certificateNameParameter = certificateName.AsProvisioningParameter(module);
        var customDomainParameter = customDomain.AsProvisioningParameter(module);

        var bindingTypeConditional = new ConditionalExpression(
            new BinaryExpression(
                new IdentifierExpression(certificateNameParameter.BicepIdentifier),
                BinaryBicepOperator.NotEqual,
                new StringLiteralExpression(string.Empty)),
            new StringLiteralExpression("SniEnabled"),
            new StringLiteralExpression("Disabled")
            );

        var certificateOrEmpty = new ConditionalExpression(
            new BinaryExpression(
                new IdentifierExpression(certificateNameParameter.BicepIdentifier),
                BinaryBicepOperator.NotEqual,
                new StringLiteralExpression(string.Empty)),
            new InterpolatedStringExpression(
                [
                    containerAppManagedEnvironmentId.Compile(),
                    new StringLiteralExpression("/managedCertificates/"),
                    new IdentifierExpression(certificateNameParameter.BicepIdentifier)
                 ]),
            new NullLiteralExpression()
            );

        var containerAppCustomDomain = new ContainerAppCustomDomain()
        {
            BindingType = bindingTypeConditional,
            Name = customDomainParameter,
            CertificateId = certificateOrEmpty
        };

        var existingCustomDomain = app.Configuration.Ingress.CustomDomains
            .FirstOrDefault(cd =>
            {
                // This is a cautionary tale to anyone who reads this code as to the dangers
                // of using implicit conversions in C#. BicepValue<T> uses some implicit conversions
                // which means we need to explicitly cast to IBicepValue so that we can get at the
                // source construct behind the Bicep value on the "name" field for a custom domain
                // in the Bicep. If the constructs are the same ProvisioningParameter then we have a
                // match - otherwise we are possibly dealing with a second domain. This deals with the
                // edge case of where someone might call ConfigureCustomDomain multiple times on the
                // same domain - unlikely but possible if someone has built some libraries.                
                var itemDomainNameBicepValue = cd.Value?.Name as IBicepValue;
                var candidateDomainNameBicepValue = containerAppCustomDomain.Name as IBicepValue;
                return itemDomainNameBicepValue?.Source?.Construct == candidateDomainNameBicepValue.Source?.Construct;
            });

        if (existingCustomDomain is not null)
        {
            app.Configuration.Ingress.CustomDomains.Remove(existingCustomDomain);
        }

        app.Configuration.Ingress.CustomDomains.Add(containerAppCustomDomain);
    }

    /// <summary>
    /// Allows configuring the specified compute resource as an Azure Container App Job.
    /// </summary>
    /// <typeparam name="T">The type of the compute resource.</typeparam>
    /// <param name="resource">The compute resource builder.</param>
    /// <param name="configure">The configuration action for the container app job.</param>
    /// <returns>The updated compute resource builder.</returns>
    /// <remarks>
    /// This method adds the necessary infrastructure for container app jobs to the application builder
    /// and applies the specified configuration to the container app job.
    /// 
    /// Note that the default trigger type for the job is set to Manual, and the default replica timeout is set to 1800 seconds (30 minutes).
    /// 
    /// <example>
    /// <code>
    /// builder.AddProject&lt;Projects.Api&gt;.PublishAsAzureContainerAppJob((infrastructure, job) =>
    /// {
    ///     // Configure the container app job here
    ///     job.Configuration.TriggerType = ContainerAppJobTriggerType.Schedule;
    ///     job.Configuration.ScheduleTriggerConfig.CronExpression = "0 0 * * *"; // every day at midnight
    /// });
    /// </code>
    /// </example>
    /// <para>This overload allows custom configuration of the container app job via a callback.</para>
    /// </remarks>
    [AspireExport("publishAsConfiguredAzureContainerAppJob", Description = "Configures the compute resource as an Azure Container App Job with custom configuration")]
    [Experimental("ASPIREAZURE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> PublishAsAzureContainerAppJob<T>(this IResourceBuilder<T> resource, Action<AzureResourceInfrastructure, ContainerAppJob> configure)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configure);

        if (!resource.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return resource;
        }

        resource.ApplicationBuilder.AddAzureContainerAppsInfrastructureCore();

        resource.WithAnnotation(new AzureContainerAppJobCustomizationAnnotation(configure));

        return resource;
    }

    /// <summary>
    /// Configures the specified compute resource as a manually triggered Azure Container App Job.
    /// </summary>
    /// <typeparam name="T">The type of the compute resource.</typeparam>
    /// <param name="resource">The compute resource builder.</param>
    /// <returns>The updated compute resource builder.</returns>
    /// <remarks>
    /// This is a convenience overload for the common case of manually triggered jobs. Manual trigger 
    /// is the default trigger type, so this method provides a simpler API when no additional job 
    /// configuration is needed.
    /// 
    /// <example>
    /// <code>
    /// builder.AddProject&lt;Projects.ProcessorJob&gt;("processor-job")
    ///        .PublishAsAzureContainerAppJob(); // Manual trigger (default)
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport("publishAsAzureContainerAppJob", Description = "Configures the compute resource as a manually triggered Azure Container App Job")]
    [Experimental("ASPIREAZURE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> PublishAsAzureContainerAppJob<T>(this IResourceBuilder<T> resource)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(resource);

        return resource.PublishAsAzureContainerAppJob((infrastructure, job) =>
        {
            // Manual trigger is the default, so no additional configuration is needed
            // This overload provides a simpler API for the common manual trigger case
        });
    }

    /// <summary>
    /// Configures the specified compute resource as a scheduled Azure Container App Job with the provided cron expression.
    /// </summary>
    /// <typeparam name="T">The type of the compute resource.</typeparam>
    /// <param name="resource">The compute resource builder.</param>
    /// <param name="cronExpression">The cron expression that defines the schedule for the job.</param>
    /// <param name="configure">The configuration action for the container app job.</param>
    /// <returns>The updated compute resource builder.</returns>
    /// <remarks>
    /// This method is a convenience wrapper around <see cref="PublishAsAzureContainerAppJob{T}(IResourceBuilder{T}, Action{AzureResourceInfrastructure, ContainerAppJob})"/>
    /// that automatically configures the job with a schedule trigger using the specified cron expression.
    /// 
    /// <example>
    /// <code>
    /// builder.AddProject&lt;Projects.ProcessorJob&gt;("job")
    ///        .PublishAsScheduledAzureContainerAppJob("0 0 * * *"); // Run every day at midnight
    /// </code>
    /// </example>
    /// <para>This overload allows custom configuration of the scheduled container app job via a callback.</para>
    /// </remarks>
    [AspireExport("publishAsConfiguredScheduledAzureContainerAppJob", Description = "Configures the compute resource as a scheduled Azure Container App Job with custom configuration")]
    [Experimental("ASPIREAZURE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> PublishAsScheduledAzureContainerAppJob<T>(this IResourceBuilder<T> resource, string cronExpression, Action<AzureResourceInfrastructure, ContainerAppJob>? configure = null)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);

        return resource.PublishAsAzureContainerAppJob((infrastructure, job) =>
        {
            job.Configuration.TriggerType = ContainerAppJobTriggerType.Schedule;
            job.Configuration.ScheduleTriggerConfig.CronExpression = cronExpression;

            configure?.Invoke(infrastructure, job);
        });
    }

    /// <summary>
    /// Configures the specified compute resource as a scheduled Azure Container App Job with the provided cron expression.
    /// </summary>
    /// <typeparam name="T">The type of the compute resource.</typeparam>
    /// <param name="resource">The compute resource builder.</param>
    /// <param name="cronExpression">The cron expression that defines the schedule for the job.</param>
    /// <returns>The updated compute resource builder.</returns>
    /// <remarks>
    /// This method is a convenience wrapper that configures the job with a schedule trigger using the specified cron expression.
    /// </remarks>
    [AspireExport("publishAsScheduledAzureContainerAppJob", Description = "Configures the compute resource as a scheduled Azure Container App Job")]
    [Experimental("ASPIREAZURE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal static IResourceBuilder<T> PublishAsScheduledAzureContainerAppJob<T>(this IResourceBuilder<T> resource, string cronExpression)
        where T : IComputeResource
    {
        return resource.PublishAsScheduledAzureContainerAppJob(cronExpression, configure: null);
    }
}
