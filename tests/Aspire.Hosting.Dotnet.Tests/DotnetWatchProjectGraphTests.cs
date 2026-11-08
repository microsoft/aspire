// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias HostingDotnet;

using System.Text.Json;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.TestUtilities;
using DotnetWatchOwnership = HostingDotnet::Aspire.Shared.DotnetWatch.DotnetWatchOwnership;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetWatchProjectGraphTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task UsesEvaluatedConditionalImportedReferencesAndActiveImportPaths()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var library = fixture.Project("Library");
        var ignored = fixture.Project("Ignored");
        var imported = fixture.Write("build/active.input", """
            <Project>
              <PropertyGroup><ReferenceDirectory>$(MSBuildThisFileDirectory)../Library</ReferenceDirectory></PropertyGroup>
              <ItemGroup><ProjectReference Include="$(ReferenceDirectory)/Library.csproj" /></ItemGroup>
            </Project>
            """);
        var inactive = fixture.Write("build/inactive.input", """
            <Project><ItemGroup><ProjectReference Include="../Ignored/Ignored.csproj" /></ItemGroup></Project>
            """);
        var project = fixture.Project("App", """
            <Import Project="../build/active.input" Condition="'$(Configuration)' == 'Release'" />
            <Import Project="../build/inactive.input" Condition="'$(Configuration)' == 'Never'" />
            <Target Name="Build"><Error Text="Evaluation must not build fixture projects." /></Target>
            <Target Name="Restore"><Error Text="Evaluation must not restore fixture projects." /></Target>
            """);

        var graph = await fixture.EvaluateAsync(project, configuration: "Release");
        Assert.Equal([project, library], graph.Projects.Keys);
        var node = graph.Projects[project];
        Assert.Equal([library], node.References);
        Assert.Equal(
            new[] { System.IO.Path.Combine(fixture.Path, "Directory.Build.props"), System.IO.Path.Combine(fixture.Path, "Directory.Build.targets"), System.IO.Path.Combine(fixture.Path, "Directory.Packages.props"), imported, project }.Order(StringComparer.Ordinal),
            node.Imports.Where(path => path.StartsWith(fixture.Path, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.False(graph.Projects.ContainsKey(ignored));
        Assert.False(node.Imports.Contains(inactive, DotnetWatchProjectGraph.PathComparer));
        Assert.Equal("Release", node.Properties["Configuration"]);
        Assert.Equal(fixture.SdkDirectory, graph.SdkDirectory);
        Assert.Equal(fixture.Muxer, graph.Muxer);
        Assert.Equal(fixture.SdkVersion, graph.SdkVersion);
        Assert.False(Directory.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(project)!, "bin")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(project)!, "obj")));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task PreservesGlobalPropertyAndLowerPrecedenceEnvironmentDistinction()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var library = fixture.Project("Library");
        var project = fixture.Project("App", """
            <PropertyGroup><BuildFlavor>unconditional</BuildFlavor></PropertyGroup>
            <ItemGroup Condition="'$(BuildFlavor)' == 'requested'"><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
            """);
        var environment = new Dictionary<string, string>
        {
            ["BuildFlavor"] = "requested",
            ["EscapedValue"] = "one;two%3B$(Other),\"quoted\"\nnext",
            ["Optimize"] = "false",
        };

        var normal = await fixture.EvaluateAsync(project, "Release", environment, globalProperties: true);
        var watcher = await fixture.EvaluateAsync(project, "Release", environment, globalProperties: false);
        Assert.Equal("requested", normal.Projects[project].Properties["BuildFlavor"]);
        Assert.Equal("unconditional", watcher.Projects[project].Properties["BuildFlavor"]);
        Assert.Equal(environment["EscapedValue"], normal.Projects[project].Properties["EscapedValue"]);
        Assert.Equal("one;two;$(Other),\"quoted\"\nnext", watcher.Projects[project].Properties["EscapedValue"]);
        Assert.Equal([library], normal.Projects[project].References);
        Assert.Empty(watcher.Projects[project].References);
        Assert.False(DotnetWatchProjectGraph.Equivalent(normal, watcher));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task PreservesReleaseConfigurationAndHotReloadAdaptationWhenSdkDefaultsPermitIt()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var project = fixture.Project("App");
        var environment = new Dictionary<string, string> { ["Optimize"] = "false" };

        var normal = await fixture.EvaluateAsync(project, "Release", environment, globalProperties: true);
        var watcher = await fixture.EvaluateAsync(project, "Release", environment, globalProperties: false);
        Assert.Equal("Release", watcher.Projects[project].Properties["Configuration"]);
        Assert.Equal("false", watcher.Projects[project].Properties["Optimize"]);
        Assert.True(DotnetWatchProjectGraph.Equivalent(normal, watcher));
        Assert.Equal(normal.Projects[project].Outputs, watcher.Projects[project].Outputs);
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task EvaluatesFileAppDirectivesWithGlobalConfigurationAndSdkImports()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var release = fixture.Project("ReleaseLibrary");
        var debug = fixture.Project("DebugLibrary");
        var include = fixture.Write("shared/source.cs", "internal static class Included { }");
        var project = fixture.Write("app.cs", """
            #:property Configuration=Debug
            #:property LibraryRoot=$(Configuration)Library
            #:project $(LibraryRoot)
            #:include shared/source.cs
            Console.WriteLine("File app");
            """);
        var environment = new Dictionary<string, string>
        {
            ["StartupHookSupport"] = "true",
            ["MetadataUpdaterSupport"] = "true",
            ["Optimize"] = "false",
        };

        var normal = await fixture.EvaluateAsync(project, "Release", environment);
        var node = normal.Projects[project];
        Assert.Equal([project, release], normal.Projects.Keys);
        Assert.Equal([release], node.References);
        Assert.Equal("Release", node.Properties["Configuration"]);
        Assert.Equal("true", node.Properties["FileBasedProgram"]);
        Assert.Equal("true", node.Properties["StartupHookSupport"]);
        Assert.Equal("true", node.Properties["MetadataUpdaterSupport"]);
        Assert.Equal(
            new[] { project, include }.Order(StringComparer.Ordinal),
            node.Imports.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal));
        Assert.False(normal.Projects.ContainsKey(debug));
        Assert.False(File.Exists(project + ".csproj"));

        var queried = await fixture.QueryPropertiesAsync(
            project,
            ["Configuration", "TargetFramework", "TargetPath", "OutputPath", "IntermediateOutputPath", "RunCommand", "RunArguments"],
            "Release",
            environment);
        foreach (var (name, value) in queried)
        {
            Assert.Equal(value, node.Properties[name]);
        }
        // The source's unconditional Debug configuration cannot round-trip through watcher environment values.
        var watcher = await fixture.EvaluateAsync(project, "Release", environment, globalProperties: false);
        Assert.Equal([debug], watcher.Projects[project].References);
        Assert.False(DotnetWatchProjectGraph.Equivalent(normal, watcher));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task FileAppDefaultConfigurationSupportsEnvironmentAdaptation()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var project = fixture.Write("app.cs", "Console.WriteLine(\"File app\");");
        var environment = new Dictionary<string, string>
        {
            ["StartupHookSupport"] = "true",
            ["MetadataUpdaterSupport"] = "true",
            ["Optimize"] = "false",
        };

        var normal = await fixture.EvaluateAsync(project, "Release", environment);
        var watcher = await fixture.EvaluateAsync(project, "Release", environment, globalProperties: false);
        Assert.Equal("Release", watcher.Projects[project].Properties["Configuration"]);
        Assert.Equal("true", watcher.Projects[project].Properties["StartupHookSupport"]);
        Assert.True(DotnetWatchProjectGraph.Equivalent(normal, watcher));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task ExposesSdkEvaluatedFileAppCompileAndPackageChangesForGuardedRefresh()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        fixture.Write("first.cs", "internal static class First { }");
        fixture.Write("second.cs", "internal static class Second { }");
        var source = """
            #:include first.cs
            #:package Example.Package@1.0.0
            Console.WriteLine("Initial");
            """;
        var project = fixture.Write("app.cs", source);
        var first = await fixture.EvaluateAsync(project);
        fixture.Write("app.cs", source.Replace("Initial", "Method body edit", StringComparison.Ordinal));
        var methodEdit = await fixture.EvaluateAsync(project);
        Assert.Equal(first.Projects[project].Properties["CompileItems"], methodEdit.Projects[project].Properties["CompileItems"]);
        Assert.Equal(first.Projects[project].Properties["PackageReferences"], methodEdit.Projects[project].Properties["PackageReferences"]);

        fixture.Write("app.cs", source.Replace("first.cs", "second.cs", StringComparison.Ordinal).Replace("1.0.0", "2.0.0", StringComparison.Ordinal));
        var structural = await fixture.EvaluateAsync(project);
        var comparison = DotnetWatchProjectGraph.Compare(first, structural);
        Assert.Equal(DotnetWatchGraphComparisonKind.RefreshRequired, comparison.Kind);
        Assert.Contains(comparison.Details, detail => detail.Contains("CompileItems", StringComparison.Ordinal));
        Assert.Contains(comparison.Details, detail => detail.Contains("PackageReferences", StringComparison.Ordinal));
        await Verify(JsonSerializer.Serialize(new
        {
            Initial = Items(first),
            Changed = Items(structural),
        }, new JsonSerializerOptions { WriteIndented = true }), "json");

        object Items(DotnetWatchProjectGraphResult graph)
        {
            var properties = graph.Projects[project].Properties;

            return new
            {
                CompileItems = JsonSerializer.Deserialize<DotnetWatchCompileItem[]>(properties["CompileItems"])!
                    .Select(item => new { Identity = item.Identity.Replace(fixture.Path, "{fixture}", StringComparison.Ordinal).Replace('\\', '/'), FullPath = item.FullPath.Replace(fixture.Path, "{fixture}", StringComparison.Ordinal).Replace('\\', '/') }),
                PackageReferences = JsonSerializer.Deserialize<DotnetWatchPackageReference[]>(properties["PackageReferences"]),
            };
        }
    }

    [Theory]
    [InlineData("AdditionalProperties", "Configuration=Release")]
    [InlineData("SetConfiguration", "Configuration=Release")]
    [InlineData("SetPlatform", "Platform=x64")]
    [InlineData("Targets", "Build")]
    [InlineData("GlobalPropertiesToRemove", "Configuration")]
    [RequiresTools(["dotnet"])]
    public async Task DiagnosesUnrepresentableReferenceContextBeforeExecutingTargets(string name, string value)
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        fixture.Project("Library");
        var project = fixture.Project("App", $$"""
            <ItemGroup><ProjectReference Include="../Library/Library.csproj" {{name}}="{{value}}" /></ItemGroup>
            <Target Name="Build"><Error Text="Evaluation must not build fixture projects." /></Target>
            """);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => fixture.EvaluateAsync(project));
        Assert.Contains(name, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("profile.pubxml", false)]
    [RequiresTools(["dotnet"])]
    public async Task AllowsDefaultWebPublishProfileRemovalOnlyWhenNoProfileIsSelected(string profile, bool supported)
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var library = fixture.Project("Library");
        var project = fixture.Project("App", $$"""
            <PropertyGroup><WebPublishProfileFile>{{profile}}</WebPublishProfileFile></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Library/Library.csproj" GlobalPropertiesToRemove=";WebPublishProfileFile" /></ItemGroup>
            """);

        if (supported)
        {
            var graph = await fixture.EvaluateAsync(project);
            Assert.Equal([library], graph.Projects[project].References);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => fixture.EvaluateAsync(project));
            Assert.Contains("GlobalPropertiesToRemove", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task RejectsCustomRunArgumentExecutionWithoutRunningIt()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var marker = System.IO.Path.Combine(fixture.Path, "target-ran");
        var project = fixture.Project("App", $$"""
            <Target Name="CustomRunArguments" BeforeTargets="ComputeRunArguments">
              <WriteLinesToFile File="{{marker}}" Lines="executed" />
            </Target>
            """);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => fixture.EvaluateAsync(project));
        Assert.Contains("ComputeRunArguments", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task KeepsRootSdkSelectionIndependentOfEvaluationStartupDirectory()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var project = fixture.Project("App");
        var startup = System.IO.Path.Combine(fixture.Path, "different-startup");
        fixture.PinSdk(startup, "99.0.100");
        var before = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name!)
            .Where(name => name.StartsWith("Microsoft.Build", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

        var normal = await fixture.EvaluateAsync(project);
        var watcher = await fixture.EvaluateAsync(project, workingDirectory: startup);
        Assert.Equal(fixture.SdkDirectory, watcher.SdkDirectory);
        Assert.Equal(fixture.Muxer, watcher.Muxer);
        Assert.Equal(startup, watcher.Projects[project].Properties["MSBuildStartupDirectory"]);
        Assert.True(DotnetWatchProjectGraph.Equivalent(normal, watcher));
        Assert.Equal(
            before,
            AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name!)
                .Where(name => name.StartsWith("Microsoft.Build", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task FencesOutputChangesDependentOnEvaluationStartupDirectory()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var project = fixture.Project("App", """
            <PropertyGroup><OutputPath>$(MSBuildStartupDirectory)/outputs/</OutputPath></PropertyGroup>
            """);
        var startup = Directory.CreateDirectory(System.IO.Path.Combine(fixture.Path, "different-startup")).FullName;

        var normal = await fixture.EvaluateAsync(project);
        var watcher = await fixture.EvaluateAsync(project, workingDirectory: startup);
        Assert.False(DotnetWatchProjectGraph.Equivalent(normal, watcher));
        Assert.NotEqual(normal.Projects[project].Outputs, watcher.Projects[project].Outputs);
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task DiagnosesUnrepresentedAutomaticResponseFileWithoutExecutingIt()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var project = fixture.Project("App");
        fixture.Write("Directory.Build.rsp", "-property:Configuration=Release");

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => fixture.EvaluateAsync(project));
        Assert.Contains("Directory.Build.rsp", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task DoesNotForwardPrivateAppHostOwnershipOrNotificationState()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var project = fixture.Project("App", """
            <PropertyGroup>
              <ObservedOwner>$(ASPIRE_DOTNET_WATCH_OWNER)</ObservedOwner>
              <ObservedControl>$(ASPIRE_DOTNET_WATCH_HOST_CONTROL)</ObservedControl>
              <ObservedSession>$(ASPIRE_DOTNET_WATCH_HOST_SESSION)</ObservedSession>
            </PropertyGroup>
            """);
        var environment = DotnetWatchOwnership.PrivateEnvironmentVariables.ToDictionary(name => name, _ => "private");
        environment["ObservedOwner"] = "";
        environment["ObservedControl"] = "";
        environment["ObservedSession"] = "";

        var graph = await fixture.EvaluateAsync(project, environment: environment, globalProperties: false);
        Assert.Equal("", graph.Projects[project].Properties["ObservedOwner"]);
        Assert.Equal("", graph.Projects[project].Properties["ObservedControl"]);
        Assert.Equal("", graph.Projects[project].Properties["ObservedSession"]);
        Assert.All(DotnetWatchOwnership.PrivateEnvironmentVariables, name => Assert.False(graph.Projects[project].Properties.ContainsKey(name)));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task AnchorsMixedSeparatorOutputPathsToPhysicalProjectInsteadOfStartupDirectory()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var project = fixture.Project("App", """
            <PropertyGroup>
              <OutputPath>..\shared\bin\</OutputPath>
              <BaseIntermediateOutputPath>..\shared\obj\</BaseIntermediateOutputPath>
              <IntermediateOutputPath>..\shared\obj\custom\</IntermediateOutputPath>
              <MSBuildProjectExtensionsPath>..\shared\extensions\</MSBuildProjectExtensionsPath>
            </PropertyGroup>
            """);

        var graph = await fixture.EvaluateAsync(project, workingDirectory: fixture.Path);
        var node = graph.Projects[project];
        Assert.Equal(
            new[]
            {
                System.IO.Path.Combine(fixture.Path, "shared", "bin", "net10.0"),
                System.IO.Path.Combine(fixture.Path, "shared", "obj"),
                System.IO.Path.Combine(fixture.Path, "shared", "obj", "custom", "net10.0"),
                System.IO.Path.Combine(fixture.Path, "shared", "extensions"),
                System.IO.Path.Combine(fixture.Path, "shared", "bin", "net10.0", "App.dll"),
            }.Select(DotnetWatchProjectGraph.Normalize).Order(StringComparer.Ordinal),
            node.Outputs.Order(StringComparer.Ordinal));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task EvaluatedReferencesAndOutputDomainsDetermineBuildIsolation()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        fixture.Project("Shared");
        var left = fixture.Project("Left", """<ItemGroup><ProjectReference Include="../Shared/Shared.csproj" /></ItemGroup>""");
        var right = fixture.Project("Right", """<ItemGroup><ProjectReference Include="../Shared/Shared.csproj" /></ItemGroup>""");
        var independent = fixture.Project("Independent");
        var sharedOutput = fixture.Project("SharedOutput", """<PropertyGroup><OutputPath>../Left/bin/$(Configuration)/</OutputPath></PropertyGroup>""");

        var leftGraph = await fixture.EvaluateAsync(left);
        var rightGraph = await fixture.EvaluateAsync(right);
        var independentGraph = await fixture.EvaluateAsync(independent);
        var outputGraph = await fixture.EvaluateAsync(sharedOutput);
        Assert.True(DotnetWatchProjectGraph.SharesOutputs(leftGraph, rightGraph));
        Assert.True(DotnetWatchProjectGraph.SharesOutputs(leftGraph, outputGraph));
        Assert.False(DotnetWatchProjectGraph.SharesOutputs(leftGraph, independentGraph));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task DiagnosesSdkResolverReplacementInsteadOfInventingRootSdkFidelity()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var project = fixture.Project("App");
        var foreign = Directory.CreateDirectory(System.IO.Path.Combine(fixture.Path, "foreign-sdk")).FullName;

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            fixture.EvaluateAsync(project, environment: new Dictionary<string, string> { ["MSBuildSDKsPath"] = foreign }));
        Assert.Contains("MSBuildSDKsPath", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task MultiTargetedRootsRequireSelectionAndReferencedLibrariesUseTheirParentFramework()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);
        var multi = fixture.Write("Multi/Multi.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net10.0;net11.0</TargetFrameworks></PropertyGroup>
            </Project>
            """);
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => fixture.EvaluateAsync(multi));
        Assert.Contains("requires an explicit target framework", exception.Message, StringComparison.Ordinal);

        var selected = await fixture.EvaluateAsync(
            multi,
            environment: new Dictionary<string, string> { ["TargetFramework"] = "net10.0" });
        Assert.Equal("net10.0", selected.Projects[DotnetWatchProjectGraph.Normalize(multi)].Properties["TargetFramework"]);

        var app = fixture.Project("App", """<ItemGroup><ProjectReference Include="../Multi/Multi.csproj" /></ItemGroup>""");
        var graph = await fixture.EvaluateAsync(app);
        Assert.Equal("net10.0", graph.Projects[DotnetWatchProjectGraph.Normalize(multi)].Properties["TargetFramework"]);
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task DiagnosesCyclesWithoutBuilding()
    {
        using var fixture = await DotnetWatchEvaluationFixture.CreateAsync(output);

        var first = fixture.Project("First", """<ItemGroup><ProjectReference Include="../Second/Second.csproj" /></ItemGroup>""");
        fixture.Project("Second", """<ItemGroup><ProjectReference Include="../First/First.csproj" /></ItemGroup>""");
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => fixture.EvaluateAsync(first));
        Assert.Contains("cyclic reference graph", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationIsNotClassifiedAsPreflightFallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DotnetWatchProjectGraph.EvaluateGraphAsync(
            System.IO.Path.GetFullPath("App.csproj"), null, new Dictionary<string, string>(), true, Environment.CurrentDirectory, cancellation.Token));
    }

    [Theory]
    [InlineData("output", "output/nested", true)]
    [InlineData("output", "output-neighbor", false)]
    [InlineData("output/left", "output/right", false)]
    [InlineData("output", "output", true)]
    public void SharesOutputsUsesPhysicalDomainsAndDirectoryBoundaries(string firstOutput, string secondOutput, bool expected)
    {
        var left = Graph("left.csproj", firstOutput);
        var right = Graph("right.csproj", secondOutput);
        Assert.Equal(expected, DotnetWatchProjectGraph.SharesOutputs(left, right));
        Assert.Equal(expected, DotnetWatchProjectGraph.SharesOutputs(right, left));
        Assert.True(DotnetWatchProjectGraph.SharesOutputs(left, left));
    }

    [Fact]
    public void GraphComparisonSeparatesRefreshableAndIncompatibleChanges()
    {
        var first = Graph("App.csproj", "output");
        var path = first.Projects.Keys.Single();
        var node = first.Projects[path];
        var refreshedProperties = node.Properties.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        refreshedProperties["DefineConstants"] = "TRACE;WATCH_IMPORTED";
        Assert.Equal(DotnetWatchGraphComparisonKind.RefreshRequired,
            DotnetWatchProjectGraph.Compare(first, WithNode(first, node with { Imports = [System.IO.Path.GetFullPath("new.input")] })).Kind);
        Assert.Equal(DotnetWatchGraphComparisonKind.RefreshRequired,
            DotnetWatchProjectGraph.Compare(first, WithNode(first, node with { References = [System.IO.Path.GetFullPath("New.csproj")] })).Kind);
        Assert.Equal(DotnetWatchGraphComparisonKind.RefreshRequired,
            DotnetWatchProjectGraph.Compare(first, WithNode(first, node with
            {
                Properties = refreshedProperties
            })).Kind);
        Assert.Equal(DotnetWatchGraphComparisonKind.Incompatible,
            DotnetWatchProjectGraph.Compare(first, first with { SdkVersion = "10.0.404" }).Kind);
        Assert.Equal(DotnetWatchGraphComparisonKind.Incompatible,
            DotnetWatchProjectGraph.Compare(first, first with { SdkDirectory = System.IO.Path.GetFullPath("different-sdk") }).Kind);
        Assert.Equal(DotnetWatchGraphComparisonKind.Incompatible,
            DotnetWatchProjectGraph.Compare(first, first with { Muxer = System.IO.Path.GetFullPath("different-dotnet") }).Kind);
        Assert.Equal(DotnetWatchGraphComparisonKind.Incompatible,
            DotnetWatchProjectGraph.Compare(first, WithNode(first, node with { Outputs = [System.IO.Path.GetFullPath("new-output")] })).Kind);
        Assert.Equal(DotnetWatchGraphComparisonKind.Incompatible,
            DotnetWatchProjectGraph.Compare(first, WithNode(first, node with { Properties = new Dictionary<string, string> { ["Configuration"] = "Release" } })).Kind);
        Assert.Equal(DotnetWatchGraphComparisonKind.RefreshRequired,
            DotnetWatchProjectGraph.Compare(first, Graph("Different.csproj", "output")).Kind);
    }

    [Fact]
    public void GraphComparisonCanonicalizesEquivalentOutputPathPropertySpelling()
    {
        var projectPath = DotnetWatchProjectGraph.Normalize(System.IO.Path.GetFullPath("App/App.csproj"));
        var projectDirectory = System.IO.Path.GetDirectoryName(projectPath)!;
        var outputPath = DotnetWatchProjectGraph.Normalize(System.IO.Path.Combine(projectDirectory, "bin", "Debug", "net10.0"));
        var intermediatePath = DotnetWatchProjectGraph.Normalize(System.IO.Path.Combine(projectDirectory, "obj", "Debug", "net10.0"));
        var ordinaryNode = new DotnetWatchProjectNode(
            projectPath,
            new Dictionary<string, string>
            {
                ["Configuration"] = "Debug",
                ["OutputPath"] = System.IO.Path.Combine("bin", "Debug", "net10.0"),
                ["OutDir"] = System.IO.Path.Combine("bin", "Debug", "net10.0"),
                ["BaseOutputPath"] = "bin",
                ["IntermediateOutputPath"] = System.IO.Path.Combine("obj", "Debug", "net10.0")
            },
            [],
            [],
            [outputPath, intermediatePath]);
        var watcherNode = ordinaryNode with
        {
            Properties = new Dictionary<string, string>
            {
                ["Configuration"] = "Debug",
                ["OutputPath"] = outputPath,
                ["OutDir"] = outputPath,
                ["BaseOutputPath"] = System.IO.Path.Combine(projectDirectory, "bin"),
                ["IntermediateOutputPath"] = intermediatePath
            }
        };
        var ordinary = new DotnetWatchProjectGraphResult(
            System.IO.Path.GetFullPath("sdk/10.0.403"),
            System.IO.Path.GetFullPath("dotnet"),
            "10.0.403",
            new Dictionary<string, DotnetWatchProjectNode>(DotnetWatchProjectGraph.PathComparer)
            {
                [projectPath] = ordinaryNode
            });
        var watcher = WithNode(ordinary, watcherNode);

        Assert.Equal(DotnetWatchGraphComparisonKind.Equivalent, DotnetWatchProjectGraph.Compare(ordinary, watcher).Kind);

        var incompatibleProperties = watcherNode.Properties.ToDictionary(pair => pair.Key, pair => pair.Value);
        incompatibleProperties["BaseOutputPath"] = System.IO.Path.Combine(projectDirectory, "different");
        var incompatible = WithNode(watcher, watcherNode with { Properties = incompatibleProperties });
        var comparison = DotnetWatchProjectGraph.Compare(ordinary, incompatible);
        Assert.Equal(DotnetWatchGraphComparisonKind.Incompatible, comparison.Kind);
        Assert.Contains(comparison.Details, detail => detail.Contains("BaseOutputPath", StringComparison.Ordinal));
    }

    [Fact]
    public void FileAppWatcherAdaptationsAreAllowedOnlyOnTheSanctionedRoot()
    {
        var ordinary = Graph("app.cs", "output");
        var rootPath = ordinary.Projects.Keys.Single();
        var dependencyPath = DotnetWatchProjectGraph.Normalize(System.IO.Path.GetFullPath("Dependency.csproj"));
        var ordinaryRoot = ordinary.Projects[rootPath] with
        {
            Properties = new Dictionary<string, string>
            {
                ["StartupHookSupport"] = "false",
                ["MetadataUpdaterSupport"] = "false"
            }
        };
        var ordinaryDependency = new DotnetWatchProjectNode(
            dependencyPath,
            new Dictionary<string, string> { ["StartupHookSupport"] = "false" },
            [],
            [],
            [DotnetWatchProjectGraph.Normalize(System.IO.Path.GetFullPath("dependency-output"))]);
        ordinary = ordinary with
        {
            Projects = new Dictionary<string, DotnetWatchProjectNode>(DotnetWatchProjectGraph.PathComparer)
            {
                [rootPath] = ordinaryRoot,
                [dependencyPath] = ordinaryDependency
            }
        };
        var watcherRoot = ordinaryRoot with
        {
            Properties = new Dictionary<string, string>
            {
                ["StartupHookSupport"] = "true",
                ["MetadataUpdaterSupport"] = "true"
            }
        };
        var rootOnlyAdaptation = ordinary with
        {
            Projects = new Dictionary<string, DotnetWatchProjectNode>(DotnetWatchProjectGraph.PathComparer)
            {
                [rootPath] = watcherRoot,
                [dependencyPath] = ordinaryDependency
            }
        };

        Assert.Equal(
            DotnetWatchGraphComparisonKind.Equivalent,
            DotnetWatchProjectGraph.Compare(ordinary, rootOnlyAdaptation, rootPath).Kind);

        var transitiveProjects = rootOnlyAdaptation.Projects.ToDictionary(pair => pair.Key, pair => pair.Value, DotnetWatchProjectGraph.PathComparer);
        transitiveProjects[dependencyPath] = ordinaryDependency with
        {
            Properties = new Dictionary<string, string> { ["StartupHookSupport"] = "true" }
        };
        var transitiveAdaptation = rootOnlyAdaptation with { Projects = transitiveProjects };
        Assert.Equal(
            DotnetWatchGraphComparisonKind.Incompatible,
            DotnetWatchProjectGraph.Compare(ordinary, transitiveAdaptation, rootPath).Kind);
    }

    [Fact]
    public void NormalizesSymlinksAndFilesystemCasing()
    {
        var directory = Directory.CreateDirectory(System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "watch-evaluation-tests", Guid.NewGuid().ToString("N")));
        using var workspace = new TemporaryWorkspace(output, directory);
        var physical = workspace.CreateDirectory("Physical").FullName;
        var project = System.IO.Path.Combine(physical, "App.csproj");
        File.WriteAllText(project, "<Project />");
        var alias = System.IO.Path.Combine(workspace.Path, "Alias");
        try
        {
            Directory.CreateSymbolicLink(alias, physical);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // Windows can require developer mode for symlink creation; the existing filesystem-casing assertion
            // below remains applicable without that privilege.
            alias = physical;
        }
        Assert.Equal(DotnetWatchProjectGraph.Normalize(project), DotnetWatchProjectGraph.Normalize(System.IO.Path.Combine(alias, "App.csproj")));
        var otherCase = System.IO.Path.Combine(physical, "APP.CSPROJ");
        if (File.Exists(otherCase))
        {
            Assert.Equal(DotnetWatchProjectGraph.Normalize(project), DotnetWatchProjectGraph.Normalize(otherCase));
        }
    }

    private static DotnetWatchProjectGraphResult Graph(string project, string output)
    {
        var path = DotnetWatchProjectGraph.Normalize(System.IO.Path.GetFullPath(project));
        var node = new DotnetWatchProjectNode(path, new Dictionary<string, string> { ["Configuration"] = "Debug" }, [], [], [DotnetWatchProjectGraph.Normalize(System.IO.Path.GetFullPath(output))]);

        return new(
            System.IO.Path.GetFullPath("sdk/10.0.403"),
            System.IO.Path.GetFullPath("dotnet"),
            "10.0.403",
            new Dictionary<string, DotnetWatchProjectNode>(DotnetWatchProjectGraph.PathComparer) { [path] = node });
    }

    private static DotnetWatchProjectGraphResult WithNode(DotnetWatchProjectGraphResult graph, DotnetWatchProjectNode node) =>
        graph with { Projects = new Dictionary<string, DotnetWatchProjectNode>(DotnetWatchProjectGraph.PathComparer) { [node.Path] = node } };
}
