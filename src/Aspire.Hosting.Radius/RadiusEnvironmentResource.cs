// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRERADIUS006 // Secret-store validation/apply steps are experimental; consumed internally by the integration.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius.Publishing;

namespace Aspire.Hosting.Radius;

/// <summary>
/// Represents a Radius compute environment in the Aspire app model.
/// </summary>
[AspireExport(ExposeProperties = true)]
public sealed class RadiusEnvironmentResource : Resource, IComputeEnvironmentResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RadiusEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the Radius environment resource.</param>
    /// <remarks>
    /// Registers the publish/deploy pipeline steps as annotations on the resource so any
    /// caller that adds this resource to the application model gets a working environment.
    /// In Run mode the resource is normally not added to the model (see
    /// <c>AddRadiusEnvironment</c>) and the annotations are inert. Mirrors
    /// <c>KubernetesEnvironmentResource</c> / <c>DockerComposeEnvironmentResource</c>, which
    /// also keep their step factories on the resource itself rather than the extension method.
    /// </remarks>
    public RadiusEnvironmentResource(string name) : base(name)
    {
        // Single multi-step annotation matches KubernetesEnvironmentResource so a wrapper
        // integration (or any caller that constructs the resource directly) gets a complete,
        // self-contained publish pipeline. Run-mode safety comes from the resource not being
        // registered with the application builder in Run mode, not from a guard here.
        Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            // Per-environment prepare step: materializes DeploymentTargetAnnotations on
            // compute resources scoped to this environment. ValidateComputeEnvironments
            // (a DependsOn) fails-fast on multi-env ambiguity before this step runs, and
            // RequiredBy(BeforeStart) makes the prepared targets observable to downstream
            // publishing code.
            var prepareStep = new PipelineStep
            {
                Name = $"prepare-deployment-targets-{Name}",
                Description = $"Prepares Radius deployment targets for {Name}.",
                Action = stepContext => RadiusInfrastructure.PrepareDeploymentTargetsAsync(this, stepContext),
                DependsOnSteps = [WellKnownPipelineSteps.ValidateComputeEnvironments],
                RequiredBySteps = [WellKnownPipelineSteps.BeforeStart],
            };

            var publishStep = new RadiusBicepPublishingContext(this).CreatePipelineStep();
            var deployStep = new RadiusDeploymentPipelineStep(this).CreatePipelineStep();

            // Fail-fast Radius secret-store validation gate: RequiredBy this environment's
            // publish and deploy steps so type/mode/key/encoding/duplicate-name failures
            // surface before any Bicep is emitted or kubectl/rad is contacted. It is a no-op
            // when the model declares no secret stores, keeping the default path unchanged.
            var validateSecretStoresStep = new PipelineStep
            {
                Name = $"validate-radius-secret-stores-{Name}",
                Description = $"Validates Radius secret stores for {Name}.",
                Action = Secrets.RadiusSecretStoreValidation.ValidateAsync,
                RequiredBySteps = [publishStep.Name, deployStep.Name],
            };

            // Sealed-secrets apply/wait gate: applies each SealedSecret manifest to the workspace's
            // cluster and waits for the underlying Secret to materialize before rad deploy. Scheduled
            // after publish and RequiredBy deploy; a no-op when no sealed store is declared.
            var applySealedSecretsStep = new SealedSecretApplyStep(this).CreatePipelineStep();

            // Only schedule the credential-register step when the environment
            // has cloud-provider configuration attached. Apps without the new
            // WithAzure/WithAws extensions emit byte-identical pipelines.
            var hasCloudProviders = Annotations
                .OfType<Annotations.RadiusCloudProvidersAnnotation>()
                .Any();
            if (hasCloudProviders)
            {
                var registerStep = new RadCredentialRegisterStep(this).CreatePipelineStep();
                return [validateSecretStoresStep, prepareStep, publishStep, registerStep, applySealedSecretsStep, deployStep];
            }

            return [validateSecretStoresStep, prepareStep, publishStep, applySealedSecretsStep, deployStep];
        }));
    }

    /// <summary>
    /// Gets or sets the Kubernetes namespace for resource deployment.
    /// </summary>
    public string Namespace { get; set; } = "default";

    /// <summary>
    /// Gets or sets the parent compute environment this Radius environment is hosted by, when
    /// the Radius env is itself a child of a higher-level compute environment (e.g. an Azure
    /// AKS environment that wraps both Kubernetes and Radius). When set, resources that target
    /// the parent environment are also adopted by this Radius environment during the prepare
    /// step. Defaults to <see langword="null"/> (no parent).
    /// </summary>
    /// <remarks>
    /// Mirrors <c>KubernetesEnvironmentResource.OwningComputeEnvironment</c>. Today this is
    /// always <see langword="null"/> for vanilla Radius; the property exists so an Azure
    /// hosting integration can wrap Radius the same way Azure Kubernetes wraps the K8s
    /// integration without needing a breaking change to this type.
    /// </remarks>
    public IComputeEnvironmentResource? OwningComputeEnvironment { get; set; }

    /// <inheritdoc/>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        var resource = endpointReference.Resource;
        // Kubernetes service DNS for a resource deployed to a namespace is
        // `<service>.<namespace>.svc.cluster.local`. The namespace segment is required: without
        // it the name only resolves for callers already inside the same namespace, so cross-
        // namespace (and fully-qualified) service discovery breaks.
        //
        // Resolve the namespace of the environment the *target* resource deploys into, not this
        // environment's. With multiple Radius environments in one model (each with its own
        // WithNamespace), a WithReference from a resource in environment A to a resource in
        // environment B must emit B's namespace, otherwise B's service name would be qualified
        // with A's namespace and never resolve. `WithComputeEnvironment` is mandatory in
        // multi-environment models (enforced by the ValidateComputeEnvironments pipeline step),
        // so the target carries a ComputeEnvironmentAnnotation for this reachable cross-env case.
        // Fall back to this environment's namespace when the target resolves to no Radius
        // environment: the single-environment and AKS-wrap cases share this environment's
        // namespace, so the fallback is correct there.
        var targetNamespace = (resource.GetComputeEnvironment() as RadiusEnvironmentResource)?.Namespace ?? Namespace;
        return ReferenceExpression.Create($"{resource.Name}.{targetNamespace}.svc.cluster.local");
    }
}
