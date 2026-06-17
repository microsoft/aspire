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
    public void ContainerSnapshotAddsDisplayMetadataForDashboardProperties()
    {
        var container = Container.Create("container", "redis:latest");
        container.Spec.Command = "redis-server";
        container.Spec.Ports = [new() { ContainerPort = 6379 }];
        container.Spec.Persistent = true;
        container.Status = new ContainerStatus
        {
            ContainerId = "1234567890abcdef",
            EffectiveArgs = ["--appendonly", "yes"]
        };
        var snapshot = CreateSnapshotBuilder().ToSnapshot(container, CreatePreviousSnapshot());

        AssertHighlightedProperty(snapshot, KnownProperties.Container.Image, "Container image", isSensitive: false, sortOrder: 0);
        AssertHighlightedProperty(snapshot, KnownProperties.Container.Id, "Container ID", isSensitive: false, sortOrder: 1);
        AssertHighlightedProperty(snapshot, KnownProperties.Container.Command, "Container command", isSensitive: false, sortOrder: 2);
        AssertHighlightedProperty(snapshot, KnownProperties.Container.Args, "Container arguments", isSensitive: true, sortOrder: 3);
        AssertHighlightedProperty(snapshot, KnownProperties.Container.Ports, "Container ports", isSensitive: false, sortOrder: 4);
        AssertHighlightedProperty(snapshot, KnownProperties.Container.Lifetime, "Container lifetime", isSensitive: false, sortOrder: 5);
    }

    [Fact]
    public void ExecutableSnapshotAddsDisplayMetadataForDashboardProperties()
    {
        var executable = Executable.Create("exe", "dotnet");
        executable.Spec.WorkingDirectory = "/app";
        executable.Status = new ExecutableStatus
        {
            EffectiveArgs = ["run"],
            ProcessId = 1234
        };
        var snapshot = CreateSnapshotBuilder().ToSnapshot(executable, CreatePreviousSnapshot());

        AssertHighlightedProperty(snapshot, KnownProperties.Executable.Path, "Executable path", isSensitive: false, sortOrder: 0);
        AssertHighlightedProperty(snapshot, KnownProperties.Executable.WorkDir, "Working directory", isSensitive: false, sortOrder: 1);
        AssertHighlightedProperty(snapshot, KnownProperties.Executable.Args, "Executable arguments", isSensitive: true, sortOrder: 2);
        AssertHighlightedProperty(snapshot, KnownProperties.Executable.Pid, "Process ID", isSensitive: false, sortOrder: 3);
    }

    [Fact]
    public void ProjectSnapshotAddsDisplayMetadataForDashboardProperties()
    {
        var project = new ProjectResource("project");
        project.Annotations.Add(new TestProjectMetadata());
        project.Annotations.Add(new LaunchProfileAnnotation("https"));

        var executable = Executable.Create("project", "dotnet");
        executable.Annotate(DcpCustomResource.ResourceNameAnnotation, project.Name);
        executable.Spec.WorkingDirectory = "/app";
        executable.Status = new ExecutableStatus
        {
            EffectiveArgs = ["run"],
            ProcessId = 1234
        };

        var snapshot = CreateSnapshotBuilder(new Dictionary<string, IResource>
        {
            [project.Name] = project
        }).ToSnapshot(executable, CreatePreviousSnapshot());

        AssertDefaultProperty(snapshot, KnownProperties.Executable.Path, isSensitive: false);
        AssertDefaultProperty(snapshot, KnownProperties.Executable.WorkDir, isSensitive: false);
        AssertDefaultProperty(snapshot, KnownProperties.Executable.Args, isSensitive: true);
        AssertHighlightedProperty(snapshot, KnownProperties.Project.Path, "Project path", isSensitive: false, sortOrder: 0);
        AssertHighlightedProperty(snapshot, KnownProperties.Project.LaunchProfile, "Launch profile", isSensitive: false, sortOrder: 1);
        AssertHighlightedProperty(snapshot, KnownProperties.Executable.Pid, "Process ID", isSensitive: false, sortOrder: 2);
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

    [Fact]
    public void ExplicitStartExecutableSnapshotWithUnknownStateIsNotStarted()
    {
        var executable = Executable.Create("exe", "pwsh");
        executable.Spec.Start = false;
        executable.Status = new ExecutableStatus
        {
            State = ExecutableState.Unknown
        };

        var snapshot = CreateSnapshotBuilder().ToSnapshot(executable, CreatePreviousSnapshot());

        Assert.Equal(KnownResourceStates.NotStarted, snapshot.State?.Text);
    }

    [Fact]
    public void ExplicitStartExecutableStatusWithUnknownStateIsNotStarted()
    {
        var executable = Executable.Create("exe", "pwsh");
        executable.Spec.Start = false;
        executable.Status = new ExecutableStatus
        {
            State = ExecutableState.Unknown
        };

        var status = DcpResourceWatcher.GetResourceStatus(executable);

        Assert.Equal(KnownResourceStates.NotStarted, status.State);
    }

    [Fact]
    public void ExplicitStartExecutableSnapshotWithEmptyStateIsNotStarted()
    {
        var executable = Executable.Create("exe", "pwsh");
        executable.Spec.Start = false;
        executable.Status = new ExecutableStatus
        {
            State = ""
        };

        var snapshot = CreateSnapshotBuilder().ToSnapshot(executable, CreatePreviousSnapshot());

        Assert.Equal(KnownResourceStates.NotStarted, snapshot.State?.Text);
    }

    [Fact]
    public void ExplicitStartExecutableStatusWithEmptyStateIsNotStarted()
    {
        var executable = Executable.Create("exe", "pwsh");
        executable.Spec.Start = false;
        executable.Status = new ExecutableStatus
        {
            State = ""
        };

        var status = DcpResourceWatcher.GetResourceStatus(executable);

        Assert.Equal(KnownResourceStates.NotStarted, status.State);
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

    private static void AssertHighlightedProperty(CustomResourceSnapshot snapshot, string name, string displayName, bool isSensitive, int sortOrder)
    {
        var property = GetProperty(snapshot, name);
        Assert.Equal(displayName, property.DisplayName);
        Assert.True(property.IsHighlighted);
        Assert.Equal(isSensitive, property.IsSensitive);
        Assert.Equal(sortOrder, property.SortOrder);
    }

    private static void AssertDefaultProperty(CustomResourceSnapshot snapshot, string name, bool isSensitive)
    {
        var property = GetProperty(snapshot, name);
        Assert.Null(property.DisplayName);
        Assert.False(property.IsHighlighted);
        Assert.Equal(isSensitive, property.IsSensitive);
        Assert.Null(property.SortOrder);
    }

    private static IEnumerable<T> GetEnumerablePropertyValue<T>(CustomResourceSnapshot snapshot, string name)
    {
        var property = GetProperty(snapshot, name);
        return Assert.IsAssignableFrom<IEnumerable<T>>(property.Value);
    }

    private sealed class TestProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "/app/project.csproj";

        public LaunchSettings LaunchSettings { get; } = new()
        {
            Profiles =
            {
                ["https"] = new LaunchProfile
                {
                    CommandName = "Project"
                }
            }
        };
    }
}
