// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTS001 // Project launch defaults are experimental but needed to verify snapshot emission.
#pragma warning disable ASPIREEXTENSION001 // Debug support annotations are experimental.

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
    public void ProjectSnapshotIncludesLaunchConfigurationTypeForDebuggableProject()
    {
        var builder = DistributedApplication.CreateBuilder();
        var project = builder.AddResource(new ProjectResource("project"));
        project.Resource.Annotations.Add(new TestProjectMetadata());
        var configuredProject = project.WithProjectDefaults(new ProjectResourceOptions { ExcludeLaunchProfile = true });

        var executable = Executable.Create("project", "dotnet");
        executable.Annotate(DcpCustomResource.ResourceNameAnnotation, configuredProject.Resource.Name);
        executable.Status = new ExecutableStatus
        {
            EffectiveArgs = ["run"],
            ProcessId = 1234
        };

        var snapshot = CreateSnapshotBuilder(new Dictionary<string, IResource>
        {
            [configuredProject.Resource.Name] = configuredProject.Resource
        }).ToSnapshot(executable, CreatePreviousSnapshot());

        var launchConfigurationType = Assert.Single(snapshot.Properties, p => p.Name == KnownProperties.Resource.LaunchConfigurationType);
        Assert.Equal("project", Assert.IsType<string>(launchConfigurationType.Value));
    }

    [Theory]
    [InlineData("Run", "run", "--configuration", "--framework=net10.0")]
    [InlineData("run", "run", "-c", "-f")]
    [InlineData("run", "run", "--configuration=Release", "--framework")]
    [InlineData("run", "run", "-c=Release", "-f=net10.0")]
    [InlineData("WATCH", "watch", "--configuration", "--framework=net10.0")]
    [InlineData("watch", "watch", "-c", "-f")]
    [InlineData("watch", "watch", "--configuration=Release", "--framework")]
    [InlineData("watch", "watch", "-c=Release", "-f=net10.0")]
    public void ProjectSnapshotIncludesSafeDotNetLaunchMetadata(
        string launchCommand,
        string expectedLaunchCommand,
        string configurationArgument,
        string targetFrameworkArgument)
    {
        var project = new ProjectResource("project");
        project.Annotations.Add(new TestProjectMetadata());

        var effectiveArgs = new List<string>
        {
            launchCommand,
            "--project",
            "/app/project.csproj",
            configurationArgument
        };
        if (!configurationArgument.Contains('='))
        {
            effectiveArgs.Add("Release");
        }
        effectiveArgs.Add(targetFrameworkArgument);
        if (!targetFrameworkArgument.Contains('='))
        {
            effectiveArgs.Add("net10.0");
        }
        effectiveArgs.AddRange(["--", "--configuration", "Private", "--framework", "private"]);

        var executable = Executable.Create("project", "dotnet");
        executable.Annotate(DcpCustomResource.ResourceNameAnnotation, project.Name);
        executable.Status = new ExecutableStatus
        {
            EffectiveArgs = effectiveArgs,
            ProcessId = 1234
        };

        var snapshot = CreateSnapshotBuilder(new Dictionary<string, IResource>
        {
            [project.Name] = project
        }).ToSnapshot(executable, CreatePreviousSnapshot());

        Assert.Equal(expectedLaunchCommand, Assert.IsType<string>(GetProperty(snapshot, KnownProperties.Project.LaunchCommand).Value));
        Assert.Equal("Release", Assert.IsType<string>(GetProperty(snapshot, KnownProperties.Project.Configuration).Value));
        Assert.Equal("net10.0", Assert.IsType<string>(GetProperty(snapshot, KnownProperties.Project.TargetFramework).Value));
        Assert.True(GetProperty(snapshot, KnownProperties.Executable.Args).IsSensitive);
        Assert.False(GetProperty(snapshot, KnownProperties.Project.LaunchCommand).IsSensitive);
        Assert.False(GetProperty(snapshot, KnownProperties.Project.Configuration).IsSensitive);
        Assert.False(GetProperty(snapshot, KnownProperties.Project.TargetFramework).IsSensitive);
    }

    [Fact]
    public void ProjectSnapshotOmitsSensitiveHiddenLaunchToolMetadata()
    {
        var project = new ProjectResource("project");
        project.Annotations.Add(new TestProjectMetadata());

        var effectiveArgs = new List<string>
        {
            "run",
            "--project",
            "/app/project.csproj",
            "--configuration",
            "resolved-configuration-secret",
            "--framework=resolved-framework-secret",
        };
        var executable = Executable.Create("project", "dotnet");
        executable.Annotate(DcpCustomResource.ResourceNameAnnotation, project.Name);
        executable.Status = new ExecutableStatus
        {
            EffectiveArgs = effectiveArgs,
            ProcessId = 1234
        };
        executable.SetAnnotationAsObjectList(DcpCustomResource.ResourceAppArgsAnnotation, Array.Empty<AppLaunchArgumentAnnotation>());
        executable.SetAnnotationAsObjectList(Executable.SensitiveEffectiveArgumentIndexesAnnotation, [4, 5]);

        var previousSnapshot = CreatePreviousSnapshot() with
        {
            Properties =
            [
                new(KnownProperties.Project.Configuration, "stale-configuration"),
                new(KnownProperties.Project.TargetFramework, "stale-framework"),
            ]
        };
        var snapshot = CreateSnapshotBuilder(new Dictionary<string, IResource>
        {
            [project.Name] = project
        }).ToSnapshot(executable, previousSnapshot);

        Assert.Equal("run", GetProperty(snapshot, KnownProperties.Project.LaunchCommand).Value);
        Assert.Empty(snapshot.Properties.Where(property => property.Name == KnownProperties.Project.Configuration));
        Assert.Empty(snapshot.Properties.Where(property => property.Name == KnownProperties.Project.TargetFramework));
    }

    [Fact]
    public void ProjectSnapshotDoesNotPublishDotNetLaunchMetadataBeforeSensitiveOverride()
    {
        var project = new ProjectResource("project");
        project.Annotations.Add(new TestProjectMetadata());

        var effectiveArgs = new List<string>
        {
            "run",
            "--configuration",
            "Release",
            "--configuration",
            "resolved-configuration-secret",
        };
        var executable = Executable.Create("project", "dotnet");
        executable.Annotate(DcpCustomResource.ResourceNameAnnotation, project.Name);
        executable.Status = new ExecutableStatus
        {
            EffectiveArgs = effectiveArgs,
            ProcessId = 1234
        };
        executable.SetAnnotationAsObjectList(
            DcpCustomResource.ResourceAppArgsAnnotation,
            effectiveArgs.Select((argument, index) => new AppLaunchArgumentAnnotation(
                argument,
                isSensitive: false,
                effectiveArgumentIndex: index)));
        executable.SetAnnotationAsObjectList(Executable.SensitiveEffectiveArgumentIndexesAnnotation, [4]);

        var previousSnapshot = CreatePreviousSnapshot() with
        {
            Properties = [new(KnownProperties.Project.Configuration, "stale-configuration")]
        };
        var snapshot = CreateSnapshotBuilder(new Dictionary<string, IResource>
        {
            [project.Name] = project
        }).ToSnapshot(executable, previousSnapshot);

        Assert.Empty(snapshot.Properties.Where(property => property.Name == KnownProperties.Project.Configuration));
    }

    [Theory]
    [InlineData("run", "[env:ASPNETCORE_ENVIRONMENT=Development]", "--diagnostics")]
    [InlineData("watch", "-d")]
    public void ProjectSnapshotIncludesLaunchMetadataAfterSupportedDotNetPrefixes(
        string launchCommand,
        params string[] prefixes)
    {
        var project = new ProjectResource("project");
        project.Annotations.Add(new TestProjectMetadata());

        var executable = Executable.Create("project", "dotnet");
        executable.Annotate(DcpCustomResource.ResourceNameAnnotation, project.Name);
        executable.Status = new ExecutableStatus
        {
            EffectiveArgs = [.. prefixes, launchCommand, "--configuration", "Release", "--framework", "net10.0"],
            ProcessId = 1234
        };

        var snapshot = CreateSnapshotBuilder(new Dictionary<string, IResource>
        {
            [project.Name] = project
        }).ToSnapshot(executable, CreatePreviousSnapshot());

        Assert.Equal(launchCommand, GetProperty(snapshot, KnownProperties.Project.LaunchCommand).Value);
        Assert.Equal("Release", GetProperty(snapshot, KnownProperties.Project.Configuration).Value);
        Assert.Equal("net10.0", GetProperty(snapshot, KnownProperties.Project.TargetFramework).Value);
    }

    [Fact]
    public void ProjectSnapshotIncludesNullLaunchCommandWhenDotNetArgumentsAreMissing()
    {
        var project = new ProjectResource("project");
        project.Annotations.Add(new TestProjectMetadata());

        var executable = Executable.Create("project", "dotnet");
        executable.Annotate(DcpCustomResource.ResourceNameAnnotation, project.Name);
        executable.Status = new ExecutableStatus
        {
            ProcessId = 1234
        };

        var snapshot = CreateSnapshotBuilder(new Dictionary<string, IResource>
        {
            [project.Name] = project
        }).ToSnapshot(executable, CreatePreviousSnapshot());

        var launchCommand = GetProperty(snapshot, KnownProperties.Project.LaunchCommand);
        Assert.Null(launchCommand.Value);
        Assert.False(launchCommand.IsSensitive);
    }

    [Fact]
    public void ProjectSnapshotIncludesNullLaunchCommandWhenDotNetCommandIsUnsupported()
    {
        var project = new ProjectResource("project");
        project.Annotations.Add(new TestProjectMetadata());

        var executable = Executable.Create("project", "dotnet.exe");
        executable.Annotate(DcpCustomResource.ResourceNameAnnotation, project.Name);
        executable.Status = new ExecutableStatus
        {
            EffectiveArgs = ["publish", "--configuration", "Release"],
            ProcessId = 1234
        };

        var snapshot = CreateSnapshotBuilder(new Dictionary<string, IResource>
        {
            [project.Name] = project
        }).ToSnapshot(executable, CreatePreviousSnapshot());

        var launchCommand = GetProperty(snapshot, KnownProperties.Project.LaunchCommand);
        Assert.Null(launchCommand.Value);
        Assert.False(launchCommand.IsSensitive);
    }

    [Fact]
    public void ExecutableSnapshotPublishesLaunchConfigurationTypeOnlyWhenInstallingDebuggerCanEnableDebugging()
    {
        const string propertyName = "resource.launchConfigurationType";
        var resource = new TestDotnetProjectResource("python");
        resource.Annotations.Add(SupportsDebuggingAnnotation.Create<object>(
            resource.Name,
            "python",
            _ => Task.FromResult(new object())));

        var executable = Executable.Create(resource.Name, "python");
        executable.Annotate(DcpCustomResource.ResourceNameAnnotation, resource.Name);

        var snapshotBuilder = CreateSnapshotBuilder(new Dictionary<string, IResource>
        {
            [resource.Name] = resource
        });
        var snapshot = snapshotBuilder.ToSnapshot(executable, CreatePreviousSnapshot());

        Assert.Equal("python", GetProperty(snapshot, propertyName).Value);

        resource.Annotations.Add(new ForceProcessExecutionAnnotation());
        snapshot = snapshotBuilder.ToSnapshot(executable, snapshot);

        Assert.Empty(snapshot.Properties.Where(property => property.Name == propertyName));

        resource.Annotations.Remove(resource.Annotations.OfType<ForceProcessExecutionAnnotation>().Single());
        resource.Annotations.Add(new ContainerLifetimeAnnotation
        {
            Lifetime = ContainerLifetime.Persistent
        });
        snapshot = snapshotBuilder.ToSnapshot(executable, snapshot);
        Assert.Empty(snapshot.Properties.Where(property => property.Name == propertyName));
    }

    [Fact]
    public void ProjectSnapshotRejectsMultipleProjectMetadataAnnotations()
    {
        var project = new ProjectResource("project");
        project.Annotations.Add(new TestProjectMetadata());
        project.Annotations.Add(new OverrideTestProjectMetadata());

        var executable = Executable.Create("project", "dotnet");
        executable.Annotate(DcpCustomResource.ResourceNameAnnotation, project.Name);
        executable.Status = new ExecutableStatus
        {
            EffectiveArgs = ["run"],
            ProcessId = 1234
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CreateSnapshotBuilder(new Dictionary<string, IResource>
            {
                [project.Name] = project
            }).ToSnapshot(executable, CreatePreviousSnapshot()));

        Assert.Contains(project.Name, exception.Message);
        Assert.Contains("more than one", exception.Message);
    }

    [Fact]
    public void ExecutableWithProjectMetadataSnapshotAddsProjectPropertiesAndPreservesCustomResourceType()
    {
        // A plain ExecutableResource that carries IProjectMetadata (e.g. DotnetProjectResource, an
        // ExecutableResource launched via `dotnet run --project`) should render like a project in the
        // dashboard — with the project path + launch profile — for parity with AddProject.
        var resource = new TestDotnetProjectResource("proj");
        resource.Annotations.Add(new TestProjectMetadata());
        resource.Annotations.Add(new LaunchProfileAnnotation("https"));

        var executable = Executable.Create("proj", "dotnet");
        executable.Annotate(DcpCustomResource.ResourceNameAnnotation, resource.Name);
        executable.Spec.WorkingDirectory = "/app";
        executable.Status = new ExecutableStatus
        {
            EffectiveArgs = ["run"],
            ProcessId = 1234
        };

        var snapshotBuilder = CreateSnapshotBuilder(new Dictionary<string, IResource>
        {
            [resource.Name] = resource
        });
        var projectSnapshot = snapshotBuilder.ToSnapshot(executable, CreatePreviousSnapshot(KnownResourceTypes.Project));

        AssertHighlightedProperty(projectSnapshot, KnownProperties.Project.Path, "Project path", isSensitive: false, sortOrder: 0);
        AssertHighlightedProperty(projectSnapshot, KnownProperties.Project.LaunchProfile, "Launch profile", isSensitive: false, sortOrder: 1);

        var customSnapshot = snapshotBuilder.ToSnapshot(executable, CreatePreviousSnapshot("custom-type"));
        Assert.Equal("custom-type", customSnapshot.ResourceType);
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

    private static CustomResourceSnapshot CreatePreviousSnapshot(string resourceType = "resource")
    {
        return new()
        {
            ResourceType = resourceType,
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

    private sealed class OverrideTestProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "/app/override.csproj";

        // Launch settings are supplied inline so launch profile resolution never falls back to reading
        // Properties/launchSettings.json from disk for this fake project path.
        public LaunchSettings LaunchSettings { get; } = new()
        {
            Profiles =
            {
                ["http"] = new LaunchProfile
                {
                    CommandName = "Project"
                }
            }
        };
    }

    private sealed class TestDotnetProjectResource(string name) : ExecutableResource(name, "dotnet", "/app");
}
