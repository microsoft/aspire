// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Aspire.Hosting;

/// <summary>Configures Azure Monitor health model publishing.</summary>
public static class AzureHealthModelExtensions
{
    /// <summary>
    /// Publishes a saved health-model definition together with an Azure Monitor workspace and its
    /// Prometheus ingestion endpoint, collection rule and managed identities.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The unique Aspire resource and deployment name.</param>
    /// <param name="definitionFile">The exported model file, relative to the AppHost directory or absolute.</param>
    /// <returns>The health-model publishing resource.</returns>
    /// <remarks>
    /// This resource is not added to the run-mode application and never provisions Azure during local
    /// startup. Publish validates and reads the project-owned definition. Health signals query the
    /// <c>aspire_health_status</c> Prometheus gauge; applications or probes must publish real measurements
    /// using the documented labels and .NET HealthStatus values.
    /// </remarks>
    [AspireExport]
    [Experimental("ASPIREAZUREHEALTH001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureHealthModelResource> AddAzureHealthModel(
        this IDistributedApplicationBuilder builder, [ResourceName] string name, string definitionFile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionFile);

        var path = Path.GetFullPath(definitionFile, builder.AppHostDirectory);
        var resource = new AzureHealthModelResource(name, path)
        {
            ValidateBindings = document => HealthModelBindingValidator.Validate(document, builder.Resources, builder.Environment.ApplicationName)
        };
        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        builder.AddAzureProvisioning();
        return builder.AddResource(resource).WithIconName("Heart");
    }
}
