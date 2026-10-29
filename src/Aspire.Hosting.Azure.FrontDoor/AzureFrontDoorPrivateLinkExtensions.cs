// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for reaching a compute environment, such as Azure Container Apps, through Azure Front Door Private Link.
/// </summary>
public static class AzureFrontDoorPrivateLinkExtensions
{
    /// <summary>
    /// Connects an Azure Front Door to a compute environment, such as an Azure Container Apps environment, over Private
    /// Link and disables public network access to the environment, so that the apps in the environment are reachable only
    /// through the Front Door.
    /// </summary>
    /// <typeparam name="T">The type of the compute environment, which implements <see cref="IAzureCdnPrivateLinkTarget"/>.</typeparam>
    /// <param name="environment">The compute environment to connect to.</param>
    /// <param name="frontDoor">
    /// The Azure Front Door, created with <see cref="AzureFrontDoorExtensions.AddAzureFrontDoor"/>, whose origins in
    /// <paramref name="environment"/> use Private Link.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for <paramref name="environment"/>, for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// Add the apps to the Front Door with <see cref="AzureFrontDoorExtensions.WithOrigin"/>, before or after this call.
    /// When publishing, every origin of <paramref name="frontDoor"/> that runs in <paramref name="environment"/> becomes a
    /// Private Link origin: the Front Door profile uses the Premium SKU, and the origin connects to the environment through
    /// a Front Door managed private endpoint. The apps must expose external HTTP endpoints, for example with
    /// <c>WithExternalHttpEndpoints</c>. Origins in other compute environments are left as they are.
    /// </para>
    /// <para>
    /// Like a private endpoint to any other Azure resource, the Private Link disables public network access to the
    /// environment. Apps in the environment are no longer reachable from the Internet except through Front Door. Callers
    /// outside the environment must reach the apps through the Front Door, or through a route of their own.
    /// </para>
    /// <para>
    /// An Azure Container Apps environment can only have its public network access disabled when Aspire provisions it, so it
    /// can't be marked as existing, and it must be a workload profiles environment, not an
    /// <see href="https://learn.microsoft.com/azure/container-apps/express-overview">Azure Container Apps Express</see>
    /// environment.
    /// </para>
    /// <para>
    /// Front Door creates a connection request in the Pending state, and no traffic reaches the apps until the owner of
    /// the environment approves it. Approve it in the Azure portal, under the environment's networking settings, or with
    /// the Azure CLI by finding the request with <c>az network private-endpoint-connection list</c> and approving it with
    /// <c>az network private-endpoint-connection approve</c>. Front Door can create more than one request; approve each
    /// one that has the message of this connection. Traffic starts flowing a few minutes after approval.
    /// </para>
    /// <para>
    /// Front Door creates its private endpoint in the region of <paramref name="environment"/>. Front Door Private Link is
    /// only available in some
    /// <see href="https://learn.microsoft.com/azure/frontdoor/private-link#region-availability">regions</see>, so for an
    /// environment in another region the private endpoint is created in the supported region of the same geography, such as
    /// <c>westus2</c> for <c>westus</c>, at the cost of an extra network hop. Deployment fails when the geography has no
    /// supported region. To choose another region, set the origin's <c>SharedPrivateLinkResource.PrivateLinkLocation</c> in
    /// <see cref="AzureProvisioningResourceExtensions.ConfigureInfrastructure{T}"/> on <paramref name="frontDoor"/>, for
    /// example to a parameter.
    /// </para>
    /// <para>
    /// Private Link has no run-mode counterpart, so this method does nothing when the AppHost runs locally.
    /// </para>
    /// </remarks>
    /// <ats-remarks>
    /// <para>
    /// Add the apps to the Front Door with <c>withOrigin</c>, before or after this call. Every origin of the Front Door that
    /// runs in the environment becomes a Private Link origin: the Front Door profile uses the Premium SKU, and the origin
    /// connects to the environment through a Front Door managed private endpoint. The apps must expose external HTTP
    /// endpoints. Origins in other compute environments are left as they are.
    /// </para>
    /// <para>
    /// Public network access to the environment is disabled, so the apps are reachable only through the Front Door. The
    /// environment must be a workload profiles environment that Aspire provisions.
    /// </para>
    /// <para>
    /// Front Door creates a connection request in the Pending state, and no traffic reaches the apps until the owner of
    /// the environment approves it in the Azure portal or with <c>az network private-endpoint-connection approve</c>.
    /// </para>
    /// </ats-remarks>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> or <paramref name="frontDoor"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Publishing fails when the environment is marked as existing or is an Express environment, when
    /// <paramref name="frontDoor"/> has no origin in the environment, and when another Front Door has an origin in the
    /// environment without being connected to it.
    /// </exception>
    /// <example>
    /// <code lang="csharp">
    /// var environment = builder.AddAzureContainerAppEnvironment("env");
    /// var frontDoor = builder.AddAzureFrontDoor("frontdoor");
    /// environment.WithAzureFrontDoorPrivateLink(frontDoor);
    ///
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithExternalHttpEndpoints();
    /// var web = builder.AddProject&lt;Projects.Web&gt;("web")
    ///     .WithExternalHttpEndpoints()
    ///     .WithReference(api);
    /// frontDoor.WithOrigin(api).WithOrigin(web);
    /// </code>
    /// </example>
    /// <seealso href="https://learn.microsoft.com/azure/container-apps/how-to-integrate-with-azure-front-door">Access a container app using Azure Front Door with Private Link</seealso>
    [AspireExport("withFrontDoorAzureFrontDoorPrivateLink", MethodName = "withAzureFrontDoorPrivateLink")]
    [Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
    public static IResourceBuilder<T> WithAzureFrontDoorPrivateLink<T>(
        this IResourceBuilder<T> environment,
        IResourceBuilder<AzureFrontDoorResource> frontDoor)
        where T : IAzureCdnPrivateLinkTarget
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(frontDoor);

        if (!environment.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return environment;
        }

        // Calling this again for the same environment and Front Door has nothing more to do.
        if (frontDoor.Resource.Annotations
            .OfType<AzureFrontDoorPrivateLinkAnnotation>()
            .Any(annotation => ReferenceEquals(annotation.Environment, environment.Resource)))
        {
            return environment;
        }

        frontDoor.WithAnnotation(new AzureFrontDoorPrivateLinkAnnotation(frontDoor.Resource, environment.Resource));

        // A private link target denies public network access, as it does for a private endpoint to any other Azure
        // resource. The Front Door is the resource that creates the managed private endpoint.
        environment.WithAnnotation(new PrivateEndpointTargetAnnotation(frontDoor.Resource));

        return environment;
    }
}
