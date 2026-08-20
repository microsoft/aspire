// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

internal sealed class ExecutableResourcePreparer(
    DcpNameGenerator nameGenerator,
    DistributedApplicationModel model,
    DcpAppResourceStore appResources)
{
    private readonly DcpNameGenerator _nameGenerator = nameGenerator;
    private readonly DistributedApplicationModel _model = model;
    private readonly DcpAppResourceStore _appResources = appResources;

    public IEnumerable<RenderedModelResource<Executable>> PrepareObjects(CancellationToken cancellationToken)
    {
        PrepareProjectExecutables(cancellationToken);
        PreparePlainExecutables();

        return _appResources.Get().OfType<RenderedModelResource<Executable>>();
    }

    private void PrepareProjectExecutables(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var project in _model.GetProjectResources())
        {
            if (!project.TryGetProjectMetadata(out var projectMetadata))
            {
                throw new InvalidOperationException($"Project resource '{project.Name}' is missing required metadata.");
            }

            EnsureRequiredAnnotations(project);
            var replicas = project.GetReplicaCount();

            for (var i = 0; i < replicas; i++)
            {
                var instance = DcpExecutor.GetDcpInstance(project, instanceIndex: i);
                project.TryGetLastAnnotation<ExecutableAnnotation>(out var executableAnnotation);
                var executable = Executable.Create(instance.Name, executableAnnotation?.Command ?? "dotnet");
                executable.Spec.WorkingDirectory =
                    executableAnnotation?.WorkingDirectory ??
                    Path.GetDirectoryName(projectMetadata.ProjectPath);

                ApplyCommonAnnotations(executable, project, instance, replicas, i);
                ApplyExplicitStart(project, executable.Spec);
                DcpExecutor.SetInitialResourceState(project, executable);
                AddRenderedResource(project, executable);
            }
        }
    }

    private void PreparePlainExecutables()
    {
        foreach (var resource in _model.GetExecutableResources())
        {
            EnsureRequiredAnnotations(resource);

            var instance = DcpExecutor.GetDcpInstance(resource, instanceIndex: 0);
            var executable = Executable.Create(instance.Name, resource.Command);
            executable.Spec.WorkingDirectory = resource.WorkingDirectory;

            ApplyCommonAnnotations(executable, resource, instance, replicaCount: 1, replicaIndex: 0);
            ApplyExplicitStart(resource, executable.Spec);
            DcpExecutor.SetInitialResourceState(resource, executable);
            AddRenderedResource(resource, executable);
        }
    }

    private static void ApplyCommonAnnotations(
        Executable executable,
        IResource resource,
        DcpInstance instance,
        int replicaCount,
        int replicaIndex)
    {
        executable.Annotate(CustomResource.OtelServiceNameAnnotation, resource.Name);
        executable.Annotate(CustomResource.OtelServiceInstanceIdAnnotation, resource.GetOtelServiceInstanceId(instance));
        executable.Annotate(CustomResource.ResourceNameAnnotation, resource.Name);
        executable.Annotate(CustomResource.ResourceReplicaCount, replicaCount.ToString(CultureInfo.InvariantCulture));
        executable.Annotate(CustomResource.ResourceReplicaIndex, replicaIndex.ToString(CultureInfo.InvariantCulture));
    }

    private static void ApplyExplicitStart(IResource resource, ExecutableSpec spec)
    {
        if (resource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _))
        {
            spec.Start = false;
        }
    }

    private void EnsureRequiredAnnotations(IResource resource)
    {
        resource.AddLifeCycleCommands();
        _nameGenerator.EnsureDcpInstancesPopulated(resource);
    }

    private void AddRenderedResource(IResource resource, Executable executable)
    {
        var renderedResource = new RenderedModelResource<Executable>(resource, executable);
        DcpModelUtilities.AddServicesProducedInfo(renderedResource, _appResources.Get());
        _appResources.Add(renderedResource);
    }
}
