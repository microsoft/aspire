// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class FallbackProjectParserTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions s_indentedOptions = new() { WriteIndented = true };

    private static string FormatJson(JsonDocument document)
    {
        return JsonSerializer.Serialize(document.RootElement, s_indentedOptions);
    }

    [Fact]
    public async Task ParseProject_ExtractsAspireAppHostSdk_OldFormat()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.csproj");
        var projectContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0-test" />
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert
        await Verify(FormatJson(result), extension: "json");
    }

    [Fact]
    public async Task ParseProject_ExtractsAspireAppHostSdk_NewFormat()
    {
        // Arrange - tests the new <Project Sdk="Aspire.AppHost.Sdk/version"> format
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.csproj");
        var projectContent = """
            <Project Sdk="Aspire.AppHost.Sdk/13.0.1">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                </PropertyGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert
        await Verify(FormatJson(result), extension: "json");
    }

    [Fact]
    public async Task ParseProject_ExtractsAspireAppHostSdk_NewFormat_WithMultipleSdks()
    {
        // Arrange - tests parsing when multiple SDKs are in the attribute
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.csproj");
        var projectContent = """
            <Project Sdk="Aspire.AppHost.Sdk/13.0.1;Microsoft.NET.Sdk">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                </PropertyGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert - should extract only the version, not the other SDK
        await Verify(FormatJson(result), extension: "json");
    }

    [Fact]
    public async Task ParseProject_DoesNotMatchSimilarSdkName()
    {
        // Arrange - tests that Aspire.AppHost.SdkFoo doesn't match
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.csproj");
        var projectContent = """
            <Project Sdk="Aspire.AppHost.SdkFoo/1.0.0">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                </PropertyGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert - should fall back to old format and get 9.5.0
        await Verify(FormatJson(result), extension: "json");
    }

    [Fact]
    public async Task ParseProject_ExtractsPackageReferences()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.csproj");
        var projectContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0-test" />
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.AppHost" Version="9.5.0-test" />
                    <PackageReference Include="Aspire.Hosting.Redis" Version="9.4.1" />
                </ItemGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert
        await Verify(FormatJson(result), extension: "json");
    }

    [Fact]
    public async Task ParseProject_ExtractsProjectReferences()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.csproj");
        var projectContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0-test" />
                <ItemGroup>
                    <ProjectReference Include="../ServiceDefaults/ServiceDefaults.csproj" />
                    <ProjectReference Include="../WebApp/WebApp.csproj" />
                </ItemGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert - scrub the temp path and normalize path separators for cross-platform consistency
        // Note: In JSON, backslashes are escaped as \\, so we need to replace \\\\ (which represents \\)
        // with / to get a single forward slash
        var tempPath = Path.GetTempPath().Replace("\\", "/");
        await Verify(FormatJson(result), extension: "json")
            .ScrubLinesWithReplace(line =>
            {
                // First normalize JSON-escaped backslashes (\\) to forward slashes
                // In JSON, a single backslash is represented as \\, so we replace that with /
                line = line.Replace("\\\\", "/");
                // Then replace the temp path
                line = line.Replace($"/private{tempPath}", "{TempPath}"); // Handle macOS temp symlinks
                line = line.Replace(tempPath, "{TempPath}");
                return line;
            });
    }

    [Fact]
    public async Task ParseProject_InvalidXml_ThrowsProjectUpdaterException()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.csproj");
        var invalidProjectContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0-test" />
                <!-- Missing closing tag -->
                <ItemGroup>
                    <PackageReference Include="Test" Version="1.0.0" />
            """;

        await File.WriteAllTextAsync(projectFile, invalidProjectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act & Assert
        Assert.Throws<ProjectUpdaterException>(() =>
            parser.ParseProject(new FileInfo(projectFile)));
    }

    [Fact]
    public async Task ParseProject_SingleFileAppHost_ExtractsAspireAppHostSdk()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.cs");
        var projectContent = """
            #:sdk Aspire.AppHost.Sdk@13.0.0-preview.1.25519.5
            #:package Aspire.Hosting.NodeJs@9.5.1

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert
        await Verify(FormatJson(result), extension: "json");
    }

    [Fact]
    public async Task ParseProject_SingleFileAppHost_ExtractsPackageReferences()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.cs");
        var projectContent = """
            #:sdk Aspire.AppHost.Sdk@13.0.0-preview.1.25519.5
            #:package Aspire.Hosting.NodeJs@9.5.1
            #:package Aspire.Hosting.Python@9.5.1
            #:package Aspire.Hosting.Redis@9.5.1
            #:package CommunityToolkit.Aspire.Hosting.NodeJS.Extensions@9.8.0

            #pragma warning disable ASPIREHOSTINGPYTHON001

            var builder = DistributedApplication.CreateBuilder(args);
            var cache = builder.AddRedis("cache");
            builder.Build().Run();
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert
        await Verify(FormatJson(result), extension: "json");
    }

    [Fact]
    public async Task ParseProject_SingleFileAppHost_NoPackageReferences()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.cs");
        var projectContent = """
            #:sdk Aspire.AppHost.Sdk@9.5.0

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert
        await Verify(FormatJson(result), extension: "json");
    }

    [Fact]
    public async Task ParseProject_SingleFileAppHost_WithWildcardVersion()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.cs");
        var projectContent = """
            #:sdk Aspire.AppHost.Sdk@*
            #:package Aspire.Hosting.Redis@*

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert
        await Verify(FormatJson(result), extension: "json");
    }

    [Fact]
    public async Task ParseProject_SingleFileAppHost_NoProjectReferences()
    {
        // Arrange - single-file apphosts don't support project references
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.cs");
        var projectContent = """
            #:sdk Aspire.AppHost.Sdk@9.5.0

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert
        await Verify(FormatJson(result), extension: "json");
    }

    [Fact]
    public async Task ParseProject_SingleFileAppHost_NoSdkDirective()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.cs");
        var projectContent = """
            // Missing SDK directive
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """;

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act
        var result = parser.ParseProject(new FileInfo(projectFile));

        // Assert
        await Verify(FormatJson(result), extension: "json");
    }

    [Fact]
    public async Task ParseProject_UnsupportedFileType_ThrowsProjectUpdaterException()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(output);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.txt");
        var projectContent = "Some random content";

        await File.WriteAllTextAsync(projectFile, projectContent);
        var parser = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance);

        // Act & Assert
        var exception = Assert.Throws<ProjectUpdaterException>(() =>
            parser.ParseProject(new FileInfo(projectFile)));
        Assert.Contains("Unsupported project file type", exception.Message);
    }
}
