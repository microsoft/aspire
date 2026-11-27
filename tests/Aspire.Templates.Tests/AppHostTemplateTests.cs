// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Aspire.Templates.Tests;

public partial class AppHostTemplateTests : TemplateTestsBase
{
    public AppHostTemplateTests(ITestOutputHelper testOutput)
        : base(testOutput)
    {
    }

    [Theory]
    [InlineData("aspire-apphost", "--no-restore")]
    [InlineData("aspire", "--no-restore")]
    [InlineData("aspire-starter", "--skipRestore")]
    [InlineData("aspire-ts-cs-starter", "--skipRestore")]
    [Trait("category", "basic-build")]
    public async Task AppHostIsDetectableBeforeRestore(string template, string skipRestoreOption)
    {
        var id = GetNewProjectId(prefix: "apphost_detection");
        var buildEnvironment = BuildEnvironment.ForDefaultFramework;
        await using var project = await AspireProject.CreateNewTemplateProjectAsync(
            id,
            template,
            _testOutput,
            buildEnvironment,
            extraArgs: skipRestoreOption,
            addEndpointsHook: false);
        var appHostDirectory = template == "aspire-apphost" ? project.RootDir : project.AppHostProjectDirectory;
        var projectPath = Assert.Single(Directory.EnumerateFiles(appHostDirectory, "*.csproj"));

        Assert.False(Directory.Exists(Path.Combine(appHostDirectory, "obj")));

        using var command = new DotNetCommand(_testOutput, buildEnv: buildEnvironment);
        command.WithWorkingDirectory(appHostDirectory);
        var result = await command.ExecuteAsync(
            $"msbuild \"{projectPath}\" --ignore:.sln -nologo --getProperty:IsAspireHost --getItem:ProjectCapability");
        result.EnsureSuccessful();

        // azd detects AppHosts before restore using this JSON shape:
        // {"Properties":{"IsAspireHost":"true"},"Items":{"ProjectCapability":[{"Identity":"Aspire"}]}}
        // https://github.com/Azure/azure-dev/blob/main/cli/azd/pkg/tools/dotnet/dotnet.go
        using var document = JsonDocument.Parse(result.Output);
        var isAspireHost = document.RootElement.GetProperty("Properties").GetProperty("IsAspireHost").GetString() == "true";
        var hasAspireCapability = document.RootElement.GetProperty("Items").GetProperty("ProjectCapability")
            .EnumerateArray().Any(capability => capability.GetProperty("Identity").GetString() == "Aspire");

        Assert.True(isAspireHost || hasAspireCapability, "azd must recognize the generated AppHost before package restore.");
    }

    [Fact]
    public async Task EnsureProjectsReferencing8_1_0AppHostWithNewerWorkloadCanBuild()
    {
        string projectId = "aspire-can-reference-8.1.0";
        await using var project = await AspireProject.CreateNewTemplateProjectAsync(
            projectId,
            "aspire-apphost",
            _testOutput,
            BuildEnvironment.ForDefaultFramework,
            addEndpointsHook: false);

        var projectPath = Path.Combine(project.RootDir, $"{projectId}.csproj");

        // Replace the reference to Aspire.Hosting.AppHost with version 8.1.0
        var newContents = AppHostPackageReferenceRegex().Replace(File.ReadAllText(projectPath), @"$1""8.1.0""");

        File.WriteAllText(projectPath, newContents);

        // Ensure project builds successfully
        await project.BuildAsync(workingDirectory: project.RootDir);
    }

    [GeneratedRegex(@"(PackageReference\s.*""Aspire\.Hosting\.AppHost.*Version=)""[^""]+""")]
    private static partial Regex AppHostPackageReferenceRegex();
}
