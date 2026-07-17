// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius;
using Aspire.Hosting.Radius.Annotations;
using Aspire.Hosting.Radius.CloudProviders;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>
/// Cloud-provider configuration entry points for
/// <see cref="RadiusEnvironmentResource"/>. These extensions attach a
/// <see cref="RadiusCloudProvidersAnnotation"/> to the environment that the
/// publisher consumes to emit <c>providers.azure</c>/<c>providers.aws</c>
/// scope blocks and to schedule <c>rad credential register</c> invocations
/// during deploy.
/// </summary>
public static class RadiusCloudProviderExtensions
{
    /// <summary>
    /// Attaches an Azure cloud provider to the Radius environment. The
    /// <paramref name="configure"/> callback selects exactly one credential
    /// mode (Service Principal or Workload Identity); omitting a selection
    /// is an error.
    /// </summary>
    /// <param name="builder">The Radius environment resource builder.</param>
    /// <param name="subscriptionId">Azure subscription GUID.</param>
    /// <param name="resourceGroup">Resource-group name within the subscription.</param>
    /// <param name="configure">Callback that selects a credential mode.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentException">Validation failed on inputs.</exception>
    /// <exception cref="InvalidOperationException">The callback did not select a credential (ASPIRERADIUS010).</exception>
    // [AspireExportIgnore]: the callback parameter exposes the in-flight provider
    // builder interface, which Aspire's ATS exporter (ASPIREEXPORT008) doesn't
    // know how to render. The interface is part of the public C# API surface;
    // the export is suppressed only for the ATS catalog.
    [AspireExportIgnore]
    [Experimental("ASPIRERADIUS003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<RadiusEnvironmentResource> WithAzureProvider(
        this IResourceBuilder<RadiusEnvironmentResource> builder,
        string subscriptionId,
        string resourceGroup,
        Action<IAzureRadiusProviderBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        CloudProviderValidation.ValidateGuid(subscriptionId, nameof(subscriptionId));
        CloudProviderValidation.ValidateNonEmpty(resourceGroup, nameof(resourceGroup));
        ArgumentNullException.ThrowIfNull(configure);

        var providerBuilder = new AzureRadiusProviderBuilder();
        configure(providerBuilder);

        if (providerBuilder.Credential is null)
        {
            throw new InvalidOperationException(
                "WithAzureProvider requires a credential mode to be selected " +
                "via the configure callback (e.g. azure.WithServicePrincipal(...)). " +
                "Diagnostic: ASPIRERADIUS010.");
        }

        var config = new AzureRadiusProviderConfig(subscriptionId, resourceGroup, providerBuilder.Credential);
        RadiusCloudProvidersAnnotation.GetOrAdd(builder.Resource).Azure = config;
        return builder;
    }

    /// <summary>
    /// Attaches an AWS cloud provider to the Radius environment. The
    /// <paramref name="configure"/> callback selects exactly one credential
    /// mode (Access Key or IRSA); omitting a selection is an error.
    /// </summary>
    /// <param name="builder">The Radius environment resource builder.</param>
    /// <param name="accountId">12-digit AWS account ID.</param>
    /// <param name="region">AWS region code (e.g. <c>us-west-2</c>).</param>
    /// <param name="configure">Callback that selects a credential mode.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentException">Validation failed on inputs.</exception>
    /// <exception cref="InvalidOperationException">The callback did not select a credential (ASPIRERADIUS010).</exception>
    [AspireExportIgnore]
    [Experimental("ASPIRERADIUS003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<RadiusEnvironmentResource> WithAwsProvider(
        this IResourceBuilder<RadiusEnvironmentResource> builder,
        string accountId,
        string region,
        Action<IAwsRadiusProviderBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        CloudProviderValidation.ValidateAwsAccountId(accountId, nameof(accountId));
        CloudProviderValidation.ValidateNonEmpty(region, nameof(region));
        ArgumentNullException.ThrowIfNull(configure);

        var providerBuilder = new AwsRadiusProviderBuilder();
        configure(providerBuilder);

        if (providerBuilder.Credential is null)
        {
            throw new InvalidOperationException(
                "WithAwsProvider requires a credential mode to be selected " +
                "via the configure callback (e.g. aws.WithAccessKey(...) or aws.WithIrsa(...)). " +
                "Diagnostic: ASPIRERADIUS010.");
        }

        var config = new AwsRadiusProviderConfig(accountId, region, providerBuilder.Credential);
        RadiusCloudProvidersAnnotation.GetOrAdd(builder.Resource).Aws = config;
        return builder;
    }
}
