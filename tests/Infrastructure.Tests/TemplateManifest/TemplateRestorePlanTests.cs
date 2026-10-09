// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.Json;
using System.Xml.Linq;
using Aspire.TestUtilities;
using GenerateTemplateManifest;
using Xunit;

namespace Infrastructure.Tests.TemplateManifest;

public sealed class TemplateRestorePlanTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DeduplicatesTemplatesAndFrameworksWithoutCombiningPackageChoices()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = workspace.CreateDirectory("source").FullName;
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Common" Version="1.0.0" />
                <!--#if (UseRedis) -->
                <PackageReference Include="Optional" Version="2.0.0" />
                <!--#endif -->
              </ItemGroup>
            </Project>
            """;
        WriteTemplate(source, "first", content);
        WriteTemplate(source, "second", content);

        var plan = new TemplateRestorePlan();
        await plan.DiscoverAsync(source, source, workspace.CreateDirectory("work").FullName);

        Assert.Equal(8, plan.ConfigurationCount);
        Assert.Equal(8, plan.ProjectCount);
        Assert.Equal(2, plan.Projects.Count);
        foreach (var project in plan.Projects)
        {
            Assert.Equal(["net10.0", "net8.0"], project.Frameworks);
        }
        Assert.Equal(
            [1, 2],
            plan.Projects.Select(p => p.Project.Descendants("PackageReference").Count()).Order());
    }

    [Fact]
    public async Task PreservesComputedSymbolsSourceExclusionsAndConflictingVersions()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = workspace.CreateDirectory("source").FullName;
        WriteTemplate(source, "choices", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <!--#if (IsNew) -->
                <PackageReference Include="Shared" Version="2.0.0" />
                <!--#else -->
                <PackageReference Include="Shared" Version="1.0.0" />
                <!--#endif -->
              </ItemGroup>
            </Project>
            """, """
            {
              "type": "parameter", "datatype": "choice", "defaultValue": "Old",
              "choices": [{"choice":"None"}, {"choice":"Old"}, {"choice":"New"}]
            }
            """);
        var configPath = Path.Combine(source, "choices", ".template.config", "template.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["symbols"]!["IsNew"] = JsonNode.Parse("""{"type":"computed","value":"(Mode == \"New\")"}""");
        config["sources"] = JsonNode.Parse("""[{"modifiers":[{"condition":"(Mode == \"None\")","exclude":["*.csproj"]}]}]""");
        File.WriteAllText(configPath, config.ToJsonString());

        var plan = new TemplateRestorePlan();
        await plan.DiscoverAsync(source, source, workspace.CreateDirectory("work").FullName);

        Assert.Equal(6, plan.ConfigurationCount);
        Assert.Equal(4, plan.ProjectCount);
        Assert.Equal(2, plan.Projects.Count);
        Assert.Equal(["1.0.0", "2.0.0"], plan.Projects
            .Select(p => p.Project.Descendants("PackageReference").Single().Attribute("Version")!.Value).Order());
    }

    [Fact]
    public async Task PreservesProjectEdgesAndDependencyMetadata()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = workspace.CreateDirectory("source").FullName;
        WriteTemplate(source, "edges", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>
              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
                <ProjectReference Include="Child.csproj" />
                <PackageReference Include="Root" Version="1.0.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(source, "edges", "Child.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Child" Version="1.0.0" /></ItemGroup>
            </Project>
            """);

        var plan = new TemplateRestorePlan();
        await plan.DiscoverAsync(source, source, workspace.CreateDirectory("work").FullName);

        Assert.Equal(2, plan.Projects.Count);
        var parent = Assert.Single(plan.Projects, p => p.Project.Descendants("ProjectReference").Any());
        var child = Assert.Single(plan.Projects, p => !p.Project.Descendants("ProjectReference").Any());
        Assert.Equal($"../{child.Name}/{child.Name}.csproj", parent.Project.Descendants("ProjectReference").Single().Attribute("Include")!.Value);
        Assert.Equal("true", parent.Project.Descendants("UseMicrosoftTestingPlatformRunner").Single().Value);
        Assert.Equal("all", parent.Project.Descendants("PackageReference").Single().Attribute("PrivateAssets")!.Value);
        Assert.Equal("Microsoft.AspNetCore.App", parent.Project.Descendants("FrameworkReference").Single().Attribute("Include")!.Value);
    }

    [Fact]
    public void DoesNotReorderConditionalOrOverriddenProperties()
    {
        var project = XElement.Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><Mode>first</Mode></PropertyGroup>
              <PropertyGroup Condition="'$(Mode)' == 'first'"><Mode>second</Mode></PropertyGroup>
            </Project>
            """);

        var normalized = TemplateRestorePlan.Normalize(project);

        Assert.Equal(["first", "second"], normalized.Descendants("Mode").Select(e => e.Value));
        Assert.Equal("'$(Mode)' == 'first'", normalized.Elements("PropertyGroup").Last().Attribute("Condition")!.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["pwsh"])]
    public async Task PreservesItemDeclarationOrderDuringRestore(bool separateGroups)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var environment = new TemplateManifestTestEnvironment(workspace, output);
        TemplateManifestTestEnvironment.CreatePackage(environment.ExternalFeed, "External.ItemOrderFixture", "1.0.0");
        var source = XElement.Parse($"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <TemplatePackage Include="External.ItemOrderFixture" Version="1.0.0" />
                {(separateGroups ? "</ItemGroup><ItemGroup>" : "")}
                <PackageReference Include="@(TemplatePackage)" />
              </ItemGroup>
            </Project>
            """);

        foreach (var normalize in new[] { false, true })
        {
            var directory = workspace.CreateDirectory(normalize ? "normalized" : "original").FullName;
            var path = Path.Combine(directory, "App.csproj");
            new XDocument(normalize ? TemplateRestorePlan.Normalize(source) : source).Save(path);
            using var command = environment.CreateRestoreCommand(path);

            var result = await command.ExecuteAsync();
            result.EnsureSuccessful();

            using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "obj", "project.assets.json")));
            Assert.Equal(["External.ItemOrderFixture/1.0.0"],
                assets.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name));
            using var evaluation = JsonDocument.Parse(result.Output);
            var package = Assert.Single(evaluation.RootElement.GetProperty("Items").GetProperty("PackageReference").EnumerateArray());
            Assert.Equal("External.ItemOrderFixture", package.GetProperty("Identity").GetString());
            Assert.Equal("1.0.0", package.GetProperty("Version").GetString());
        }
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task DoesNotMovePropertiesPastAChooseThatSelectsDependencies()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = XElement.Parse("""
            <Project>
              <PropertyGroup><UsePrimary>true</UsePrimary></PropertyGroup>
              <Choose>
                <When Condition="'$(UsePrimary)' == 'true'">
                  <ItemGroup><PackageReference Include="Primary" Version="1.0.0" /></ItemGroup>
                </When>
                <Otherwise>
                  <ItemGroup><PackageReference Include="WrongBranch" Version="2.0.0" /></ItemGroup>
                </Otherwise>
              </Choose>
              <PropertyGroup><OtherProperty>value</OtherProperty></PropertyGroup>
            </Project>
            """);
        var path = Path.Combine(workspace.Path, "normalized.proj");
        new XDocument(TemplateRestorePlan.Normalize(source)).Save(path);
        var script = Path.Combine(workspace.Path, "evaluate.ps1");
        File.WriteAllText(script, """
            & $env:TEST_DOTNET msbuild $env:TEST_PROJECT -nologo -getItem:PackageReference
            exit $LASTEXITCODE
            """);
        using var command = new PowerShellCommand(script, output)
            .WithTimeout(TimeSpan.FromMinutes(1))
            .WithEnvironmentVariable("TEST_DOTNET", Path.Combine(RepoRoot.Path, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            .WithEnvironmentVariable("TEST_PROJECT", path);

        var result = await command.ExecuteAsync();
        result.EnsureSuccessful();

        using var evaluation = JsonDocument.Parse(result.Output);
        var package = Assert.Single(evaluation.RootElement.GetProperty("Items").GetProperty("PackageReference").EnumerateArray());
        Assert.Equal("Primary", package.GetProperty("Identity").GetString());
        Assert.Equal("1.0.0", package.GetProperty("Version").GetString());
    }

    [Theory]
    [InlineData("<Import Project=\"external.props\" />")]
    [InlineData("<Target Name=\"Collect\"><Message Text=\"$(Property)\" /></Target>")]
    public void PreservesGroupsAroundOtherTopLevelElements(string element)
    {
        var source = XElement.Parse($"""
            <Project>
              <PropertyGroup><Property>before</Property></PropertyGroup>
              {element}
              <ItemGroup><PackageReference Include="Example" Version="1.0.0" /></ItemGroup>
            </Project>
            """);

        Assert.True(XNode.DeepEquals(source, TemplateRestorePlan.Normalize(source)));
    }

    [Fact]
    public void RejectsUnboundedDependencyParameters()
    {
        var config = JsonNode.Parse("""
            {"symbols":{"PackageVersion":{"type":"parameter","datatype":"string","defaultValue":"1.0.0"}}}
            """)!.AsObject();

        Assert.Throws<InvalidDataException>(() => TemplateRestorePlan.GetConfigurations(config, "$(PackageVersion)").ToArray());
    }

    [Fact]
    public async Task PreservesFileBasedSdkAndPropertiesAcrossFrameworks()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = workspace.CreateDirectory("source").FullName;
        WriteTemplate(source, "single", "<Project />");
        File.Delete(Path.Combine(source, "single", "App.csproj"));
        File.WriteAllText(Path.Combine(source, "single", "apphost.cs"), """
            #:sdk Aspire.AppHost.Sdk@13.5.4
            #:property AspireUseCliBundle=true
            #:package Additional@1.0.0
            """);

        var plan = new TemplateRestorePlan();
        await plan.DiscoverAsync(source, source, workspace.CreateDirectory("work").FullName);

        var project = Assert.Single(plan.Projects);
        Assert.Equal(["net10.0", "net8.0"], project.Frameworks);
        Assert.Equal(("Aspire.AppHost.Sdk", "13.5.4"), Assert.Single(plan.Sdks));
        Assert.Equal("true", project.Project.Descendants("AspireUseCliBundle").Single().Value);
        Assert.Equal("true", project.Project.Descendants("FileBasedProgram").Single().Value);
        Assert.Equal("Additional", project.Project.Descendants("PackageReference").Single().Attribute("Include")!.Value);
    }

    [Fact]
    public async Task RejectsMissingProjectReferences()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = workspace.CreateDirectory("source").FullName;
        WriteTemplate(source, "missing", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="Missing.csproj" /></ItemGroup>
            </Project>
            """);

        var plan = new TemplateRestorePlan();
        await Assert.ThrowsAsync<InvalidDataException>(() => plan.DiscoverAsync(source, source, workspace.CreateDirectory("work").FullName));
    }

    private static void WriteTemplate(string source, string identity, string project, string? mode = null)
    {
        var directory = Path.Combine(source, identity);
        Directory.CreateDirectory(Path.Combine(directory, ".template.config"));
        File.WriteAllText(Path.Combine(directory, "App.csproj"), project);
        var config = JsonNode.Parse("""
            {
              "identity": "", "name": "", "shortName": "", "symbols": {
                "Framework": {"type":"parameter","datatype":"choice","replaces":"net8.0","defaultValue":"net8.0","choices":[{"choice":"net8.0"},{"choice":"net10.0"}]},
                "UseRedis": {"type":"parameter","datatype":"bool","defaultValue":"false"},
                "Port": {"type":"parameter","datatype":"integer","defaultValue":"1234"}
              }
            }
            """)!;
        config["identity"] = identity;
        config["name"] = identity;
        config["shortName"] = identity;
        if (mode is not null)
        {
            config["symbols"]!["Mode"] = JsonNode.Parse(mode);
        }
        File.WriteAllText(Path.Combine(directory, ".template.config", "template.json"), config.ToJsonString());
    }
}
