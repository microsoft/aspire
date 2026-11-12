// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests.TemplateManifest;

public sealed class TemplateManifestBuildTargetTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["pwsh"])]
    public async Task RemovesRestoreWorkspaceOnSuccessAndFailure(bool failGeneration)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.CreateDirectory("repository with spaces").FullName;
        var intermediate = Path.Combine(root, "artifacts", "obj");
        var restore = Directory.CreateDirectory(Path.Combine(intermediate, "template-cg-restore")).FullName;
        File.WriteAllText(Path.Combine(restore, "project.assets.json"), "{}");
        var sibling = Path.Combine(intermediate, "keep.txt");
        File.WriteAllText(sibling, "unrelated output");
        var manifest = Path.Combine(root, "artifacts", "cg", "templates", "cgmanifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "stale inventory");

        // Execute the real MSBuild generation/cleanup targets with a fake generator. Template
        // preprocessing is irrelevant here; isolate cleanup and path normalization from packing.
        var templateProject = XDocument.Load(Path.Combine(RepoRoot.Path, "src", "Aspire.ProjectTemplates", "Aspire.ProjectTemplates.csproj"));
        var projectPath = Path.Combine(root, "cleanup.proj");
        var generator = Path.Combine(root, "generator.ps1");
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup",
                new XElement("RepoRoot", root + Path.DirectorySeparatorChar),
                new XElement("IntermediateOutputPath", Path.Combine(intermediate, "unused", "..")),
                new XElement("ArtifactsShippingPackagesDir", Path.Combine(root, "artifacts", "packages") + Path.DirectorySeparatorChar)),
            new XElement("Target", new XAttribute("Name", "RefreshTemplatePackageInputsForCgManifest")),
            new XElement("Target", new XAttribute("Name", "ReplacePackageVersionOnTemplates")),
            templateProject.Root!.Elements("Target").Where(target =>
                (string?)target.Attribute("Name") is "GenerateTemplateCgManifest" or "_CleanTemplateCgRestore")
                .Select(target =>
                {
                    var copy = new XElement(target);
                    copy.Element("Exec")?.SetAttributeValue("Command", $"pwsh -NoProfile -File \"{generator}\"");
                    return copy;
                }))).Save(projectPath);

        File.WriteAllText(generator, """
            if ($env:TEST_FAIL -eq 'true') { exit 1 }
            $manifest = Join-Path $env:TEST_ROOT 'artifacts/cg/templates/cgmanifest.json'
            New-Item -ItemType Directory -Path (Split-Path $manifest) -Force | Out-Null
            Set-Content -LiteralPath $manifest -Value '{"version":1,"registrations":[]}'
            """);
        var runner = Path.Combine(root, "run.ps1");
        File.WriteAllText(runner, """
            & $env:TEST_DOTNET msbuild $env:TEST_PROJECT -nologo -t:GenerateTemplateCgManifest
            exit $LASTEXITCODE
            """);
        using var command = new PowerShellCommand(runner, output)
            .WithWorkingDirectory(root)
            .WithTimeout(TimeSpan.FromMinutes(1))
            .WithEnvironmentVariable("TEST_DOTNET", Path.Combine(RepoRoot.Path, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            .WithEnvironmentVariable("TEST_PROJECT", projectPath)
            .WithEnvironmentVariable("TEST_ROOT", root)
            .WithEnvironmentVariable("TEST_FAIL", failGeneration ? "true" : "false");

        var result = await command.ExecuteAsync();

        Assert.Equal(failGeneration ? 1 : 0, result.ExitCode);
        Assert.False(Directory.Exists(restore));
        Assert.Equal("unrelated output", File.ReadAllText(sibling));
        Assert.Equal(!failGeneration, File.Exists(manifest));
    }
}
