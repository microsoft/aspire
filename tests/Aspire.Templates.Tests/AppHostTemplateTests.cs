// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using System.Text.RegularExpressions;

namespace Aspire.Templates.Tests;

public partial class AppHostTemplateTests : TemplateTestsBase
{
    public AppHostTemplateTests(ITestOutputHelper testOutput)
        : base(testOutput)
    {
    }

    [Theory]
    [InlineData("aspire-starter", false)]
    [InlineData("aspire-starter", true)]
    [InlineData("aspire-ts-cs-starter", false)]
    [InlineData("aspire-ts-cs-starter", true)]
    public async Task StarterTemplatesUseProjectDirectoriesWithTrailingSlashes(string template, bool useRedisCache)
    {
        var projectId = GetNewProjectId("starter.paths with spaces");
        await using var project = await AspireProject.CreateNewTemplateProjectAsync(
            projectId,
            template,
            _testOutput,
            BuildEnvironment.ForDefaultFramework,
            extraArgs: $"--use-redis-cache {useRedisCache.ToString().ToLowerInvariant()} --skipRestore",
            addEndpointsHook: false);

        var appHostSource = await File.ReadAllTextAsync(Path.Combine(project.AppHostProjectDirectory, "AppHost.cs"));
        var expectedProjects = template == "aspire-starter"
            ? new[] { (Name: "apiservice", Suffix: "ApiService"), (Name: "webfrontend", Suffix: "Web") }
            : new[] { (Name: "server", Suffix: "Server") };
        Assert.Equal(expectedProjects.Length, AddDotnetProjectCallRegex().Matches(appHostSource).Count);
        foreach (var (name, suffix) in expectedProjects)
        {
            var directory = $"{projectId}.{suffix}";
            Assert.Contains($"""AddDotnetProject("{name}", "../{directory}/")""", appHostSource);
            Assert.Equal(Path.Combine(project.RootDir, directory, $"{directory}.csproj"),
                Assert.Single(Directory.GetFiles(Path.Combine(project.RootDir, directory), "*.csproj")));
        }
        Assert.DoesNotContain("Aspire-StarterApplication.1", appHostSource);
        Assert.Equal(useRedisCache, appHostSource.Contains("builder.AddRedis(\"cache\")", StringComparison.Ordinal));
        Assert.Contains(".WithHttpHealthCheck(\"/health\")", appHostSource);
        Assert.Contains(".WithExternalHttpEndpoints()", appHostSource);
        Assert.Contains(".WaitFor(", appHostSource);
    }

    [GeneratedRegex(@"AddDotnetProject\(")]
    private static partial Regex AddDotnetProjectCallRegex();

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
