// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands.Sdk;

namespace Aspire.Cli.Tests.Commands;

public class IntegrationAssemblyNameResolverTests
{
    [Fact]
    public void Resolve_UsesAssemblyNameFromProjectFileWhenPresent()
    {
        using var tempDirectory = new TemporaryDirectory();
        var projectFile = tempDirectory.CreateProject(
            "TestIntegration.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>Custom.Integration.Assembly</AssemblyName>
              </PropertyGroup>
            </Project>
            """);

        var assemblyName = IntegrationAssemblyNameResolver.Resolve(projectFile);

        Assert.Equal("Custom.Integration.Assembly", assemblyName);
    }

    [Fact]
    public void Resolve_FallsBackToProjectFileNameWhenAssemblyNameIsMissing()
    {
        using var tempDirectory = new TemporaryDirectory();
        var projectFile = tempDirectory.CreateProject(
            "TestIntegration.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var assemblyName = IntegrationAssemblyNameResolver.Resolve(projectFile);

        Assert.Equal("TestIntegration", assemblyName);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aspire-cli-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public FileInfo CreateProject(string fileName, string content)
        {
            var projectPath = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(projectPath, content);
            return new FileInfo(projectPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}