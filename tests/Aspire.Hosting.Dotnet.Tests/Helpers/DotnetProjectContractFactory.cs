// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dotnet.Tests.Helpers;

internal sealed class DotnetProjectContractFactory(ITestOutputHelper output) : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    public IResourceBuilder<DotnetProjectResource> AddProject<TProject>(
        IDistributedApplicationBuilder builder, string name)
        where TProject : IProjectMetadata, new()
    {
        return builder.AddDotnetProject(name, GetProjectPath<TProject>(name));
    }

    public IResourceBuilder<DotnetProjectResource> AddProject<TProject>(
        IDistributedApplicationBuilder builder, string name, string? launchProfileName)
        where TProject : IProjectMetadata, new()
    {
        return builder.AddDotnetProject(name, GetProjectPath<TProject>(name), options =>
        {
            options.ExcludeLaunchProfile = launchProfileName is null;
            options.LaunchProfileName = launchProfileName;
        });
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private string GetProjectPath<TProject>(string name)
        where TProject : IProjectMetadata, new()
    {
        var metadata = new TProject();
        if (File.Exists(metadata.ProjectPath))
        {
            return metadata.ProjectPath;
        }

        // The shared contracts also use synthetic metadata with no physical project.
        // Give V2 real project/profile files instead of replacing its metadata after defaults.
        var directory = _workspace.CreateDirectory($"{name}-{Guid.NewGuid():N}").FullName;
        var projectPath = Path.Combine(directory, "Service.csproj");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Program.cs"), """
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/", () => "Hello World!");
            app.Run();
            """);

        if (metadata.LaunchSettings is { } launchSettings)
        {
            var properties = Directory.CreateDirectory(Path.Combine(directory, "Properties")).FullName;
            File.WriteAllText(
                Path.Combine(properties, "launchSettings.json"),
                JsonSerializer.Serialize(launchSettings, s_jsonOptions));
        }

        return projectPath;
    }
}
