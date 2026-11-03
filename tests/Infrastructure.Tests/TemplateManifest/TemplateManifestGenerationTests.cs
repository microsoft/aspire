// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests.TemplateManifest;

public sealed class TemplateManifestGenerationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Debug", false)]
    [InlineData("Release", false)]
    [InlineData("Release", true)]
    [RequiresTools(["pwsh"])]
    public async Task GeneratesEveryTimeUsingThePackedTemplateVersion(string configuration, bool customFeed)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.CreateDirectory("repository with spaces").FullName;
        var feed = customFeed ? Path.Combine(root, "custom feed") : Path.Combine(root, "artifacts", "packages", configuration, "Shipping");
        Directory.CreateDirectory(feed);
        CreateTemplatePackage(feed, "14.0.0-pr.123");
        CreateDotnetStub(root);
        var report = Path.Combine(root, "invocations.txt");

        using var command = CreateCommand()
            .WithEnvironmentVariable("TEMPLATE_CG_REPORT", report)
            .WithEnvironmentVariable("TEMPLATE_CG_EXIT_CODE", "0");
        string[] arguments = ["-RepositoryRoot", $"\"{root}\"", "-Configuration", configuration];
        if (customFeed)
        {
            arguments = [.. arguments, "-PackageDirectory", $"\"{feed}\""];
        }

        (await command.ExecuteAsync(arguments)).EnsureSuccessful();

        // An existing output must not turn the next package build into a skipped invocation.
        var manifestPath = Path.Combine(root, "artifacts", "cg", "templates", "cgmanifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, """{"version":1,"registrations":[]}""");
        (await command.ExecuteAsync(arguments)).EnsureSuccessful();

        var lines = File.ReadAllLines(report);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Contains("-target:GenerateTemplateCgManifest", line);
            Assert.Contains($"-property:Configuration={configuration}", line);
            Assert.Contains("-property:PackageVersion=14.0.0-pr.123", line);
            Assert.Contains($"-property:TemplateCgPackageDirectory={feed}", line);
        });
    }

    [Theory]
    [InlineData("missing-feed", "Shipping packages not found")]
    [InlineData("no-template-package", "Expected exactly one Aspire.ProjectTemplates package")]
    [InlineData("multiple-template-packages", "Expected exactly one Aspire.ProjectTemplates package")]
    [InlineData("failed-generation", "Template component manifest generation failed")]
    [RequiresTools(["pwsh"])]
    public async Task FailsInsteadOfSkippingMissingInputsOrFailedGeneration(string scenario, string expectedError)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var root = workspace.CreateDirectory("repo").FullName;
        var feed = Path.Combine(root, "artifacts", "packages", "Debug", "Shipping");
        if (scenario != "missing-feed")
        {
            Directory.CreateDirectory(feed);
        }
        if (scenario is "multiple-template-packages" or "failed-generation")
        {
            CreateTemplatePackage(feed, "14.0.0-pr.123");
        }
        if (scenario == "multiple-template-packages")
        {
            CreateTemplatePackage(feed, "14.0.0-pr.124");
        }
        CreateDotnetStub(root);
        using var command = CreateCommand()
            .WithEnvironmentVariable("TEMPLATE_CG_REPORT", Path.Combine(root, "invocations.txt"))
            .WithEnvironmentVariable("TEMPLATE_CG_EXIT_CODE", "1");

        var result = await command.ExecuteAsync("-RepositoryRoot", $"\"{root}\"");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.Output);
    }

    private PowerShellCommand CreateCommand() =>
        new PowerShellCommand(Path.Combine(RepoRoot.Path, "eng", "scripts", "generate-template-cgmanifest.ps1"), output)
            .WithTimeout(TimeSpan.FromMinutes(1));

    private static void CreateTemplatePackage(string directory, string version)
    {
        using var archive = ZipFile.Open(Path.Combine(directory, $"Aspire.ProjectTemplates.{version}.nupkg"), ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("Aspire.ProjectTemplates.nuspec").Open());
        writer.Write($"""
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata><id>Aspire.ProjectTemplates</id><version>{version}</version></metadata>
            </package>
            """);
    }

    private static void CreateDotnetStub(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(root, "dotnet.cmd"), """
                @echo off
                echo %*>>"%TEMPLATE_CG_REPORT%"
                exit /b %TEMPLATE_CG_EXIT_CODE%
                """);
        }
        else
        {
            var path = Path.Combine(root, "dotnet.sh");
            File.WriteAllText(path, """
                #!/bin/sh
                printf '%s\n' "$*" >> "$TEMPLATE_CG_REPORT"
                exit "$TEMPLATE_CG_EXIT_CODE"
                """);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
