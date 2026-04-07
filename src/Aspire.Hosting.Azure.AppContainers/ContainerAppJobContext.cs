// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting.Azure;

internal sealed class ContainerAppJobContext(IResource resource, ContainerAppEnvironmentContext containerAppEnvironmentContext)
    : BaseContainerAppContext(resource, containerAppEnvironmentContext)
{
    public override void BuildContainerApp(AzureResourceInfrastructure infra)
    {
        _infrastructure = infra;
        // Write a fake parameter for the container app environment
        // so azd knows the Dashboard URL - see https://github.com/microsoft/aspire/issues/8449.
        // This is temporary until a real fix can be made in azd.
        AllocateParameter(_containerAppEnvironmentContext.Environment.ContainerAppDomain);

        var containerAppIdParam = AllocateParameter(_containerAppEnvironmentContext.Environment.ContainerAppEnvironmentId);

        ProvisioningParameter? containerImageParam = null;

        if (!TryGetContainerImageName(Resource, out var containerImageName))
        {
            AllocateContainerRegistryParameters();

            containerImageParam = AllocateContainerImageParameter();
        }

        var containerAppResource = CreateContainerAppJob();

        BicepValue<string>? containerAppIdentityId = null;

        if (Resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var appIdentityAnnotation))
        {
            var appIdentityResource = appIdentityAnnotation.IdentityResource;

            containerAppIdentityId = appIdentityResource.Id.AsProvisioningParameter(infra);

            var id = BicepFunction.Interpolate($"{containerAppIdentityId}").Compile().ToString();

            containerAppResource.Identity.ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned;
            containerAppResource.Identity.UserAssignedIdentities[id] = new UserAssignedIdentityDetails();
        }

        AddContainerRegistryManagedIdentity(containerAppResource.Identity);

        containerAppResource.EnvironmentId = containerAppIdParam;

        var configuration = containerAppResource.Configuration;

        AddContainerRegistryParameters(reg => configuration.Registries = reg);

        var template = new ContainerAppJobTemplate();
        containerAppResource.Template = template;

        var containerAppContainer = new ContainerAppContainer();
        template.Containers = [containerAppContainer];

        containerAppContainer.Image = containerImageParam is null ? containerImageName! : containerImageParam;
        containerAppContainer.Name = NormalizedContainerAppName;

        SetEntryPoint(containerAppContainer);
        AddEnvironmentVariablesAndCommandLineArgs(
            containerAppContainer,
            () => configuration.Secrets ??= [],
            containerAppIdentityId);
        AddAzureClientId(appIdentityAnnotation?.IdentityResource, containerAppContainer.Env);
        AddVolumes(template.Volumes, containerAppContainer);

        infra.Add(containerAppResource);

        if (Resource.TryGetAnnotationsOfType<AzureContainerAppJobCustomizationAnnotation>(out var annotations))
        {
            foreach (var a in annotations)
            {
                a.Configure(infra, containerAppResource);
            }
        }
    }

    private ContainerAppJob CreateContainerAppJob()
    {
        var containerApp = new ContainerAppJob(Infrastructure.NormalizeBicepIdentifier(Resource.Name))
        {
            Name = NormalizedContainerAppName
        };

        var configuration = new ContainerAppJobConfiguration()
        {
            ReplicaTimeout = 1800,
            TriggerType = ContainerAppJobTriggerType.Manual,
        };
        containerApp.Configuration = configuration;

        if (Resource.HasAnnotationOfType<AzureFunctionsAnnotation>())
        {
            throw new NotSupportedException("Azure Container App Jobs are not supported with Azure Functions.");
        }

        return containerApp;
    }
}