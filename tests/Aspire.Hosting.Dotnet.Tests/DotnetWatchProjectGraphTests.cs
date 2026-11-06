// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dotnet.Watch;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetWatchProjectGraphTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("", "", true)]
    [InlineData("<GlobalPropertiesToRemove>Configuration</GlobalPropertiesToRemove>", "", false)]
    [InlineData("<AdditionalProperties>Configuration=Release</AdditionalProperties>", "", false)]
    [InlineData("<SetConfiguration>Configuration=Release</SetConfiguration>", "", false)]
    [InlineData("<SetPlatform>Platform=x64</SetPlatform>", "", false)]
    [InlineData("<SetTargetFramework>TargetFramework=net11.0</SetTargetFramework>", "", false)]
    [InlineData("<Targets>Build</Targets>", "", false)]
    [InlineData("", "Custom.pubxml", false)]
    public async Task EvaluatedWebReferencesAllowOnlyTheInactiveSdkPublishProfileRemoval(
        string referenceMetadata, string publishProfile, bool supported)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var api = Directory.CreateDirectory(Path.Combine(workspace.Path, "Api"));
        var shared = Directory.CreateDirectory(Path.Combine(workspace.Path, "Shared"));
        var path = Path.Combine(api.FullName, "Api.csproj");
        var sharedPath = DotnetWatchProjectGraph.Normalize(Path.Combine(shared.FullName, "Shared.csproj"));
        await File.WriteAllTextAsync(path, $$"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <WebPublishProfileFile>{{publishProfile}}</WebPublishProfileFile>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Shared/Shared.csproj">{{referenceMetadata}}</ProjectReference>
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(sharedPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        var metadata = new DotnetProjectMetadata(path, null, "api");
        // Select the test runner's SDK, including when its MSBuild paths are inherited.
        metadata.SetBuildWorkingDirectory(AppContext.BaseDirectory);
        if (supported)
        {
            var graph = await DotnetWatchProjectGraph.EvaluateGraphAsync(metadata, null, TestContext.Current.CancellationToken);

            Assert.Equal([DotnetWatchProjectGraph.Normalize(path), sharedPath], graph.Keys.Order(DotnetWatchProjectGraph.PathComparer));
            Assert.Equal([sharedPath], graph[DotnetWatchProjectGraph.Normalize(path)].References);
        }
        else
        {
            await Assert.ThrowsAsync<DotnetWatchToolException>(() =>
                DotnetWatchProjectGraph.EvaluateGraphAsync(metadata, null, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void IndependentProjectsHaveIndependentServers()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var api = CreateRoot(workspace.Path, "api");
        var worker = CreateRoot(workspace.Path, "worker");

        var plan = DotnetWatchProjectGraph.Partition([api, worker]);

        Assert.Empty(plan.Excluded);
        Assert.Equal(["api", "worker"], plan.Groups.Select(group => Assert.Single(group).Resource.Name).Order());
    }

    [Fact]
    public void SharedReferencesFormOneTransitiveComponent()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var first = CreateRoot(workspace.Path, "first");
        var middle = CreateRoot(workspace.Path, "middle");
        var last = CreateRoot(workspace.Path, "last");
        var left = CreateRoot(workspace.Path, "left-library").Project;
        var right = CreateRoot(workspace.Path, "right-library").Project;
        first = AddProject(first, left);
        middle = AddProject(AddProject(middle, left), right);
        last = AddProject(last, right);

        var plan = DotnetWatchProjectGraph.Partition([first, middle, last]);

        Assert.Empty(plan.Excluded);
        Assert.Equal(["first", "last", "middle"], Assert.Single(plan.Groups).Select(root => root.Resource.Name).Order());
    }

    [Fact]
    public void UnsupportedSharingMemberExcludesItsWholeComponentButNotIndependentProjects()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var api = CreateRoot(workspace.Path, "api");
        var legacy = CreateRoot(workspace.Path, "legacy") with { Reason = "A custom tool owns this launch." };
        var independent = CreateRoot(workspace.Path, "independent");
        var library = CreateRoot(workspace.Path, "library").Project;
        api = AddProject(api, library);
        legacy = AddProject(legacy, library);

        var plan = DotnetWatchProjectGraph.Partition([api, legacy, independent]);

        Assert.Same(independent, Assert.Single(Assert.Single(plan.Groups)));
        Assert.Equal(["api", "legacy"], plan.Excluded.Select(root => root.Resource.Name).Order());
        Assert.All(plan.Excluded, root => Assert.NotNull(root.Reason));
    }

    [Fact]
    public void OutputDirectoriesConnectDifferentProjectsAndRespectDirectoryBoundaries()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var first = CreateRoot(workspace.Path, "first");
        var second = CreateRoot(workspace.Path, "second");
        var separate = CreateRoot(workspace.Path, "separate");
        first = AddProject(first, first.Project with { Outputs = [Path.Combine(workspace.Path, "shared")] });
        second = AddProject(second, second.Project with { Outputs = [Path.Combine(workspace.Path, "shared", "obj")] });
        separate = AddProject(separate, separate.Project with { Outputs = [Path.Combine(workspace.Path, "shared-other")] });

        var plan = DotnetWatchProjectGraph.Partition([first, second, separate]);

        Assert.Empty(plan.Excluded);
        Assert.Equal([1, 2], plan.Groups.Select(group => group.Length).Order());
        Assert.Equal(["first", "second"], plan.Groups.Single(group => group.Length == 2).Select(root => root.Resource.Name).Order());
    }

    [Fact]
    public void DuplicateEntrypointsCannotSharePathOnlyControl()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var api = CreateRoot(workspace.Path, "api");
        var duplicate = api with { Resource = new DotnetProjectResource("duplicate", workspace.Path) };

        var plan = DotnetWatchProjectGraph.Partition([api, duplicate]);

        Assert.Empty(plan.Groups);
        Assert.Equal(2, plan.Excluded.Length);
        Assert.All(plan.Excluded, root => Assert.Equal(
            "The watcher reports project paths, not independently controlled resource registrations.", root.Reason));
    }

    [Fact]
    public void SharedOutputsWithDifferentSdkContextsRunNormally()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var first = CreateRoot(workspace.Path, "first");
        var second = CreateRoot(workspace.Path, "second");
        second = AddProject(second, second.Project with
        {
            SdkDirectory = Path.Combine(workspace.Path, "other-sdk"),
            Outputs = first.Project.Outputs
        });

        var plan = DotnetWatchProjectGraph.Partition([first, second]);

        Assert.Empty(plan.Groups);
        Assert.Equal(2, plan.Excluded.Length);
        Assert.All(plan.Excluded, root => Assert.Equal("Shared build outputs require incompatible SDK/build contexts.", root.Reason));
    }

    [Fact]
    public void SharedProjectsCannotCollapseDifferentBuildWorkingDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var first = CreateRoot(workspace.Path, "first");
        var second = CreateRoot(workspace.Path, "second");
        Assert.IsType<DotnetProjectMetadata>(second.Metadata).SetBuildWorkingDirectory(Path.Combine(workspace.Path, "other-context"));
        second = AddProject(second, first.Project);

        var plan = DotnetWatchProjectGraph.Partition([first, second]);

        Assert.Empty(plan.Groups);
        Assert.Equal(2, plan.Excluded.Length);
        Assert.All(plan.Excluded, root => Assert.Equal("Shared build outputs require incompatible SDK/build contexts.", root.Reason));
    }

    [Theory]
    [InlineData("TargetFrameworks", "net10.0;net11.0", "multi-targeted")]
    [InlineData("TargetFrameworkIdentifier", ".NETFramework", "managed watch agent")]
    [InlineData("TargetFrameworkVersion", "v5.0", "older")]
    [InlineData("OutputType", "Library", "executable")]
    [InlineData("StartupHookSupport", "false", "disables")]
    [InlineData("MetadataUpdaterSupport", "False", "disables")]
    [InlineData("PublishAot", "true", "disables")]
    [InlineData("PublishTrimmed", "True", "disables")]
    [InlineData("Optimize", "true", "disables")]
    [InlineData("RunCommand", "custom-launcher", "custom SDK")]
    [InlineData("RunArguments", "--custom", "custom SDK")]
    public void UnsupportedProjectConfigurationHasAnExplicitReason(string property, string value, string reason)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = CreateRoot(workspace.Path, "api");
        var properties = new Dictionary<string, string>(root.Project.Properties) { [property] = value };
        root = AddProject(root, root.Project with { Properties = properties });

        Assert.Contains(reason, DotnetWatchProjectGraph.GetCompatibilityReason(root, new("tool.dll", "custom", 10)));
    }

    [Fact]
    public void ReleaseConfigurationDoesNotSilentlyRunAsDebug()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = CreateRoot(workspace.Path, "api");
        root = root with { Metadata = new DotnetProjectMetadata(root.Metadata.ProjectPath, "Release", "api") };

        Assert.Equal("The watcher cannot preserve a non-Debug initial build/workspace configuration.",
            DotnetWatchProjectGraph.GetCompatibilityReason(root, new("tool.dll", "custom", 10)));
    }

    [Theory]
    [InlineData("10.0.14", 10, true)]
    [InlineData("11.0.0-rc.2.26504.105", 10, false)]
    [InlineData("11.0.0-rc.2.26504.105", 11, true)]
    public void RuntimeCompatibilityUsesTheSdkRequirementRatherThanAPackageVersionAllowlist(
        string runtime, int toolRuntime, bool compatible)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = CreateRoot(workspace.Path, "api");
        Directory.CreateDirectory(root.Project.SdkDirectory);
        File.WriteAllText(Path.Combine(root.Project.SdkDirectory, "MSBuild.runtimeconfig.json"),
            $$"""
            {
              "runtimeOptions": {
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "{{runtime}}"
                }
              }
            }
            """);

        var reason = DotnetWatchProjectGraph.GetCompatibilityReason(root, new("tool.dll", "99.0.0-custom", toolRuntime));

        if (compatible)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.Equal($"The selected SDK requires .NET {runtime}, but the watcher runs on .NET {toolRuntime}. Select a compatible explicit watcher version.", reason);
        }
    }

    [Fact]
    public void MissingSdkRuntimeMetadataDoesNotAssumeCompatibility()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = CreateRoot(workspace.Path, "api");

        Assert.Equal("The selected SDK's MSBuild runtime requirement could not be established.",
            DotnetWatchProjectGraph.GetCompatibilityReason(root, new("tool.dll", "custom", 10)));
    }

    private static DotnetWatchRoot CreateRoot(string directory, string name)
    {
        var projectDirectory = Path.Combine(directory, name);
        var path = DotnetWatchProjectGraph.Normalize(Path.Combine(projectDirectory, name + ".csproj"));
        var node = new DotnetWatchProjectNode(
            path, Path.Combine(directory, "sdk"), Path.Combine(directory, "dotnet"),
            new Dictionary<string, string>
            {
                ["TargetFramework"] = "net10.0",
                ["TargetFrameworks"] = "",
                ["TargetFrameworkIdentifier"] = ".NETCoreApp",
                ["TargetFrameworkVersion"] = "v10.0",
                ["OutputType"] = "Exe",
                ["StartupHookSupport"] = "",
                ["MetadataUpdaterSupport"] = "",
                ["PublishAot"] = "false",
                ["PublishTrimmed"] = "false",
                ["Optimize"] = "false",
                ["UseAppHost"] = "true",
                ["TargetPath"] = Path.Combine(projectDirectory, "bin", name + ".dll"),
                ["RunCommand"] = Path.Combine(projectDirectory, "bin", name + (OperatingSystem.IsWindows() ? ".exe" : "")),
                ["RunArguments"] = "",
                ["RunWorkingDirectory"] = ""
            }, [], [Path.Combine(projectDirectory, "bin"), Path.Combine(projectDirectory, "obj")]);
        var metadata = new DotnetProjectMetadata(path, null, name);
        metadata.SetBuildWorkingDirectory(directory);
        return new(new DotnetProjectResource(name, projectDirectory), metadata,
            null, new Dictionary<string, DotnetWatchProjectNode>(DotnetWatchProjectGraph.PathComparer) { [path] = node });
    }

    private static DotnetWatchRoot AddProject(DotnetWatchRoot root, DotnetWatchProjectNode project) =>
        root with
        {
            Projects = new Dictionary<string, DotnetWatchProjectNode>(root.Projects, DotnetWatchProjectGraph.PathComparer)
            {
                [project.Path] = project
            }
        };
}
