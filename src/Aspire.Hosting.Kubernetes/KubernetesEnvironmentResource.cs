// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents a Kubernetes environment resource that can host application resources.
/// </summary>
/// <remarks>
/// This resource models the Kubernetes publishing environment used by Aspire when generating
/// Helm charts and other Kubernetes manifests for application resources that will run on a
/// Kubernetes cluster.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class KubernetesEnvironmentResource : Resource, IComputeEnvironmentResource
{
    /// <summary>
    /// Gets or sets the name of the Helm chart to be generated.
    /// </summary>
    public string HelmChartName { get; set; } = "aspire";

    /// <summary>
    /// Gets or sets the version of the Helm chart to be generated.
    /// This property specifies the version number that will be assigned to the Helm chart,
    /// typically following semantic versioning conventions.
    /// </summary>
    public string HelmChartVersion { get; set; } = "0.1.0";

    /// <summary>
    /// Gets or sets the description of the Helm chart being generated.
    /// </summary>
    public string HelmChartDescription { get; set; } = "Aspire Helm Chart";

    /// <summary>
    /// Specifies the default type of storage used for Kubernetes deployments.
    /// </summary>
    /// <remarks>
    /// This property determines the storage medium used for the application.
    /// Possible values include "emptyDir", "hostPath", "pvc"
    /// </remarks>
    public string DefaultStorageType { get; set; } = "emptyDir";

    /// <summary>
    /// Specifies the default name of the storage class to be used for persistent volume claims in Kubernetes.
    /// This property allows customization of the storage class for specifying storage requirements
    /// such as performance, retention policies, and provisioning parameters.
    /// If set to null, the default storage class for the cluster will be used.
    /// </summary>
    public string? DefaultStorageClassName { get; set; }

    /// <summary>
    /// Gets or sets the default storage size for persistent volumes.
    /// </summary>
    public string DefaultStorageSize { get; set; } = "1Gi";

    /// <summary>
    /// Gets or sets the default access policy for reading and writing to the storage.
    /// </summary>
    public string DefaultStorageReadWritePolicy { get; set; } = "ReadWriteOnce";

    /// <summary>
    /// Gets or sets the default policy that determines how Docker images are pulled during deployment.
    /// Possible values are:
    /// "Always" - Always attempt to pull the image from the registry.
    /// "IfNotPresent" - Pull the image only if it is not already present locally.
    /// "Never" - Never pull the image, use only the local image.
    /// The default value is "IfNotPresent".
    /// </summary>
    public string DefaultImagePullPolicy { get; set; } = "IfNotPresent";

    /// <summary>
    /// Gets or sets the default Kubernetes service type to be used when generating artifacts.
    /// </summary>
    /// <remarks>
    /// The default value is "ClusterIP". This property determines the type of service
    /// (e.g., ClusterIP, NodePort, LoadBalancer) created in Kubernetes for the application.
    /// </remarks>
    public string DefaultServiceType { get; set; } = "ClusterIP";

    internal IPortAllocator PortAllocator { get; } = new PortAllocator();

    /// <summary>
    /// Initializes a new instance of the <see cref="KubernetesEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the Kubernetes environment.</param>
    public KubernetesEnvironmentResource(string name) : base(name)
    {
        Annotations.Add(new PipelineStepAnnotation(context =>
        {
            var step = new PipelineStep
            {
                Name = $"publish-{Name}",
                Description = $"Publishes the Kubernetes environment configuration for {Name}.",
                Action = ctx => PublishAsync(ctx)
            };
            step.RequiredBy(WellKnownPipelineSteps.Publish);
            return step;
        }));
    }

    /// <inheritdoc/>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        var resource = endpointReference.Resource;

        return ReferenceExpression.Create($"{resource.Name.ToServiceName()}");
    }

    private Task PublishAsync(PipelineStepContext context)
    {
        var outputPath = PublishingContextUtils.GetEnvironmentOutputPath(context, this);

        var kubernetesContext = new KubernetesPublishingContext(
            context.ExecutionContext,
            outputPath,
            context.Logger,
            context.CancellationToken);
        return kubernetesContext.WriteModelAsync(context.Model, this);
    }
}
