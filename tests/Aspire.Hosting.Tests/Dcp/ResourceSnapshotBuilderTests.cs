// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Tests.Dcp.ResourceSnapshotTestHelpers;
using DcpCustomResource = Aspire.Hosting.Dcp.Model.CustomResource;
using DcpResourceSnapshotBuilder = Aspire.Hosting.Dcp.ResourceSnapshotBuilder;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class ResourceSnapshotBuilderTests
{
    private const string DcpTemplateArgument = "{{- portForServing \"exe\" -}}";
    private const string ResolvedPortArgument = "52731";

    [Fact]
    public void ExecutableSnapshotUsesEffectiveArgsFromAnnotationIndexes()
    {
        var executable = CreateExecutable(
            [
                new("--port", isSensitive: false, effectiveArgumentIndex: 0),
                new(DcpTemplateArgument, isSensitive: false, effectiveArgumentIndex: 1)
            ],
            ["--port", ResolvedPortArgument]);

        var snapshot = CreateSnapshotBuilder().ToSnapshot(executable, CreatePreviousSnapshot());

        Assert.Equal(["--port", ResolvedPortArgument], GetEnumerablePropertyValue<string>(snapshot, KnownProperties.Resource.AppArgs).ToArray());
        Assert.Equal(["--port", ResolvedPortArgument], GetEnumerablePropertyValue<string>(snapshot, KnownProperties.Executable.Args).ToArray());
    }

    [Fact]
    public void ContainerSnapshotUsesEffectiveArgsFromAnnotationIndexes()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.AddContainer("aContainer", "image");

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var container = Container.Create("aContainer", "image");
        container.Annotate(DcpCustomResource.ResourceNameAnnotation, "aContainer");
        container.Spec.Args = ["--port", DcpTemplateArgument];
        container.Status = new ContainerStatus
        {
            EffectiveArgs = ["--port", ResolvedPortArgument]
        };
        container.SetAnnotationAsObjectList<AppLaunchArgumentAnnotation>(
            DcpCustomResource.ResourceAppArgsAnnotation,
            [
                new("--port", isSensitive: false, effectiveArgumentIndex: 0),
                new(DcpTemplateArgument, isSensitive: false, effectiveArgumentIndex: 1)
            ]);

        var snapshot = CreateSnapshotBuilder(distributedAppModel).ToSnapshot(container, CreatePreviousSnapshot());

        Assert.Equal(["--port", ResolvedPortArgument], GetEnumerablePropertyValue<string>(snapshot, KnownProperties.Resource.AppArgs).ToArray());
        Assert.Equal(["--port", ResolvedPortArgument], GetEnumerablePropertyValue<string>(snapshot, KnownProperties.Container.Args).ToArray());
    }

    [Fact]
    public void ExecutableSnapshotPreservesLaunchArgumentSensitivityWhenUsingEffectiveArgs()
    {
        var executable = CreateExecutable(
            [
                new("--secret", isSensitive: false, effectiveArgumentIndex: 0),
                new("{{- secretRef \"connectionString\" -}}", isSensitive: true, effectiveArgumentIndex: 1)
            ],
            ["--secret", "resolved-secret"]);

        var snapshot = CreateSnapshotBuilder().ToSnapshot(executable, CreatePreviousSnapshot());

        Assert.Equal(["--secret", "resolved-secret"], GetEnumerablePropertyValue<string>(snapshot, KnownProperties.Resource.AppArgs).ToArray());
        Assert.Equal([0, 1], GetEnumerablePropertyValue<int>(snapshot, KnownProperties.Resource.AppArgsSensitivity).ToArray());
        Assert.True(GetProperty(snapshot, KnownProperties.Resource.AppArgs).IsSensitive);
        Assert.True(GetProperty(snapshot, KnownProperties.Resource.AppArgsSensitivity).IsSensitive);
    }

    [Fact]
    public void ExecutableSnapshotFallsBackToAnnotationValueWhenEffectiveArgMissing()
    {
        var executable = CreateExecutable(
            [
                new("-port", isSensitive: false, effectiveArgumentIndex: 0),
                new(DcpTemplateArgument, isSensitive: false, effectiveArgumentIndex: 9)
            ],
            ["-port", ResolvedPortArgument]);

        var snapshot = CreateSnapshotBuilder().ToSnapshot(executable, CreatePreviousSnapshot());

        Assert.Equal(["-port", DcpTemplateArgument], GetEnumerablePropertyValue<string>(snapshot, KnownProperties.Resource.AppArgs).ToArray());
    }

    private static Executable CreateExecutable(AppLaunchArgumentAnnotation[] launchArgumentAnnotations, IReadOnlyList<string> effectiveArgs)
    {
        var executable = Executable.Create("exe", "pwsh");
        executable.Spec.Args = [.. launchArgumentAnnotations.Select(a => a.Argument)];
        executable.Status = new ExecutableStatus
        {
            EffectiveArgs = [.. effectiveArgs]
        };
        executable.SetAnnotationAsObjectList(DcpCustomResource.ResourceAppArgsAnnotation, launchArgumentAnnotations);

        return executable;
    }

    private static DcpResourceSnapshotBuilder CreateSnapshotBuilder(DistributedApplicationModel? model = null)
    {
        return new(new DcpResourceState(model?.Resources.ToDictionary(r => r.Name) ?? new Dictionary<string, IResource>(), []));
    }
}
