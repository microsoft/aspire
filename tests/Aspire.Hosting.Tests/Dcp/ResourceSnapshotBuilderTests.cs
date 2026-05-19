// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using DcpCustomResource = Aspire.Hosting.Dcp.Model.CustomResource;
using DcpResourceSnapshotBuilder = Aspire.Hosting.Dcp.ResourceSnapshotBuilder;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class ResourceSnapshotBuilderTests
{
    private const string DcpTemplateArgument = "{{- portForServing \"exe\" -}}";
    private const string ResolvedPortArgument = "52731";

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

    private static DcpResourceSnapshotBuilder CreateSnapshotBuilder(IDictionary<string, IResource>? applicationModel = null)
    {
        return new(new DcpResourceState(applicationModel ?? new Dictionary<string, IResource>(), []));
    }

    private static CustomResourceSnapshot CreatePreviousSnapshot()
    {
        return new()
        {
            ResourceType = "resource",
            Properties = []
        };
    }

    private static ResourcePropertySnapshot GetProperty(CustomResourceSnapshot snapshot, string name)
    {
        return Assert.Single(snapshot.Properties, p => p.Name == name);
    }

    private static IEnumerable<T> GetEnumerablePropertyValue<T>(CustomResourceSnapshot snapshot, string name)
    {
        var property = GetProperty(snapshot, name);
        return Assert.IsAssignableFrom<IEnumerable<T>>(property.Value);
    }
}
