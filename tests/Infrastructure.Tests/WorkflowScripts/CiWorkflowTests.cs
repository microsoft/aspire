// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class CiWorkflowTests(ITestOutputHelper output)
{
    private static readonly YamlMappingNode s_buildPackageJobs = Jobs("build-packages.yml");
    private static readonly YamlMappingNode s_testsJobs = Jobs("tests.yml");
    private static readonly YamlMappingNode s_installerJobs = Jobs("prepare-installer-artifacts.yml");
    private static readonly YamlMappingNode s_runTestsJobs = Jobs("run-tests.yml");
    private static readonly YamlMappingNode s_ciJobs = Jobs("ci.yml");

    [Fact]
    public void TemplateManifestIsGeneratedFromBuiltPackagesBeforeCleanup()
    {
        var steps = Steps(Mapping(s_buildPackageJobs, "build_packages"));
        var buildIndex = steps.FindIndex(
            step => Scalar(step, "run")?.Contains("./build.sh", StringComparison.Ordinal) == true &&
                    Scalar(step, "run")?.Contains("-pack", StringComparison.Ordinal) == true);
        var generateIndex = steps.FindIndex(
            step => Scalar(step, "run")?.Contains(
                "eng/scripts/generate-template-cgmanifest.ps1",
                StringComparison.Ordinal) == true);
        var uploadIndex = steps.FindIndex(
            step => Scalar(step, "uses")?.StartsWith("actions/upload-artifact@", StringComparison.Ordinal) == true &&
                    Scalar(Mapping(step, "with"), "name") == "template-component-manifest");
        var cleanupIndex = steps.FindIndex(
            step => Scalar(step, "run")?.Contains("rm -rf artifacts/bin", StringComparison.Ordinal) == true);

        Assert.True(buildIndex >= 0, "The package build step was not found.");
        Assert.True(generateIndex > buildIndex, "The manifest must be generated from the built packages.");
        Assert.True(uploadIndex > generateIndex, "The generated manifest must be uploaded before cleanup.");
        Assert.True(cleanupIndex > uploadIndex, "Build artifacts must remain available through manifest generation and upload.");
    }

    [Fact]
    public void TemplateManifestUploadRequiresTheFinalInventory()
    {
        var upload = Assert.Single(
            Steps(Mapping(s_buildPackageJobs, "build_packages")),
            step => Scalar(step, "uses")?.StartsWith("actions/upload-artifact@", StringComparison.Ordinal) == true &&
                    Scalar(Mapping(step, "with"), "name") == "template-component-manifest");
        var inputs = Mapping(upload, "with");

        Assert.Equal("artifacts/cg/templates/cgmanifest.json", Scalar(inputs, "path"));
        Assert.Equal("error", Scalar(inputs, "if-no-files-found"));
    }

    [Theory]
    [InlineData("prepare_winget_installer_artifacts")]
    [InlineData("prepare_homebrew_installer_artifacts")]
    public void InstallerJobsDependOnBuiltPackages(string jobName)
    {
        var job = Mapping(s_testsJobs, jobName);

        Assert.Contains("build_packages", SequenceScalars(job, "needs"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [RequiresTools(["pwsh"])]
    public async Task InstallerWorkflowStagesSameRunTemplatePackages(int shippingPackageCount)
    {
        var steps = Steps(Mapping(s_installerJobs, "prepare_installer_artifacts"));
        var download = Assert.Single(
            steps,
            step => Scalar(step, "uses")?.StartsWith("actions/download-artifact@", StringComparison.Ordinal) == true &&
                    Scalar(Mapping(step, "with"), "name") == "built-nugets");
        var downloadInputs = Mapping(download, "with");

        Assert.Equal("${{ github.workspace }}/built-nugets", Scalar(downloadInputs, "path"));

        var configure = Assert.Single(steps, step => Scalar(step, "shell") == "pwsh" &&
            Scalar(step, "run")?.Contains("ASPIRE_CLI_PACKAGES=", StringComparison.Ordinal) == true);
        var script = Assert.IsType<string>(Scalar(configure, "run"));

        using var workspace = TemporaryWorkspace.Create(output);
        var shippingDirectory = Path.Combine(workspace.Path, "built-nugets", "packages", "Shipping");
        if (shippingPackageCount > 0)
        {
            Directory.CreateDirectory(shippingDirectory);
        }

        for (var packageIndex = 0; packageIndex < shippingPackageCount; packageIndex++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(shippingDirectory, $"Aspire.ProjectTemplates.{packageIndex + 1}.0.0.nupkg"),
                string.Empty);
        }

        var nonShippingDirectory = Directory.CreateDirectory(
            Path.Combine(workspace.Path, "built-nugets", "packages", "Debug")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(nonShippingDirectory, "Aspire.ProjectTemplates.2.0.0.nupkg"),
            string.Empty);

        var scriptPath = Path.Combine(workspace.Path, "configure-cli-package-override.ps1");
        var githubEnvironmentPath = Path.Combine(workspace.Path, "github-environment");
        await File.WriteAllTextAsync(scriptPath, script);

        using var command = new PowerShellCommand(scriptPath, output)
            .WithWorkingDirectory(workspace.Path)
            .WithEnvironmentVariable("GITHUB_WORKSPACE", workspace.Path)
            .WithEnvironmentVariable("GITHUB_ENV", githubEnvironmentPath);
        var result = await command.ExecuteAsync();

        if (shippingPackageCount == 1)
        {
            result.EnsureSuccessful();
            Assert.Equal(
                [$"ASPIRE_CLI_PACKAGES={shippingDirectory}"],
                await File.ReadAllLinesAsync(githubEnvironmentPath));
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains($"found {shippingPackageCount}", result.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(githubEnvironmentPath));
        }
    }

    [Fact]
    public void RunTestsInstallsJavaForProjectsThatRequireIt()
    {
        var steps = Steps(Mapping(s_runTestsJobs, "test"));
        var javaSetup = Assert.Single(
            steps,
            step => Scalar(step, "uses")?.StartsWith("actions/setup-java@", StringComparison.Ordinal) == true);

        Assert.Equal("${{ fromJson(inputs.properties).requiresJava == true }}", Scalar(javaSetup, "if"));
        Assert.Equal("temurin", Scalar(Mapping(javaSetup, "with"), "distribution"));
        Assert.Equal("21", Scalar(Mapping(javaSetup, "with"), "java-version"));

        var properties = XDocument.Load(RepoPath("eng", "testing", "CITestsProperties.props"));
        var requiresJava = Assert.Single(
            properties.Descendants("CITestsProperty"),
            element => (string?)element.Attribute("Include") == "requiresJava");
        Assert.Equal("RequiresJava", (string?)requiresJava.Attribute("MSBuildProp"));

        var javaProject = XDocument.Load(RepoPath(
            "tests",
            "Aspire.Hosting.CodeGeneration.Java.Tests",
            "Aspire.Hosting.CodeGeneration.Java.Tests.csproj"));
        Assert.Equal("true", Assert.Single(javaProject.Descendants("RequiresJava")).Value);
    }

    [Fact]
    public void CiFailureTrackerChecksOutTheEvaluatedBranch()
    {
        var tracker = Mapping(s_ciJobs, "ci_failure_tracker");
        var checkout = Assert.Single(
            Steps(tracker),
            step => Scalar(step, "uses")?.StartsWith("actions/checkout@", StringComparison.Ordinal) == true);

        Assert.False(
            checkout.Children.TryGetValue(new YamlScalarNode("with"), out var withNode) &&
            Assert.IsType<YamlMappingNode>(withNode).Children.ContainsKey(new YamlScalarNode("ref")));
    }

    private static YamlMappingNode Jobs(string workflowName)
        => Mapping(LoadWorkflow(workflowName), "jobs");

    private static List<YamlMappingNode> Steps(YamlMappingNode job)
        => Sequence(job, "steps").Cast<YamlMappingNode>().ToList();

    private static List<string?> SequenceScalars(YamlMappingNode node, string key)
        => Sequence(node, key).Cast<YamlScalarNode>().Select(item => item.Value).ToList();

    private static YamlSequenceNode Sequence(YamlMappingNode node, string key)
        => Assert.IsType<YamlSequenceNode>(node.Children[new YamlScalarNode(key)]);

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static string RepoPath(params string[] path)
        => Path.Combine([RepoRoot.Path, .. path]);

    private static YamlMappingNode LoadWorkflow(string workflowName)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(RepoPath(".github", "workflows", workflowName)));
        yaml.Load(reader);

        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }
}
