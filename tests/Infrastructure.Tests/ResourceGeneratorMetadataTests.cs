// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Xml.Linq;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class ResourceGeneratorMetadataTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Aspire.Cli", "Resources", "ResXFileCodeGenerator")]
    [InlineData("Aspire.Dashboard", "Resources", "PublicResXFileCodeGenerator")]
    [InlineData("Aspire.Hosting", "Resources", "ResXFileCodeGenerator")]
    [InlineData("Aspire.Hosting.Azure", "Resources", "ResXFileCodeGenerator")]
    [InlineData("Aspire.Hosting.Browsers", "Resources", "ResXFileCodeGenerator")]
    [InlineData("Aspire.Hosting.DevTunnels", "Resources", "ResXFileCodeGenerator")]
    [InlineData("Aspire.Hosting.Testing", "Properties", "ResXFileCodeGenerator")]
    [RequiresTools(["pwsh"])]
    public async Task ResourceMetadataCoversExistingAndNewFiles(string projectName, string resourceDirectory, string generator)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var projectDirectory = Path.Combine(RepoRoot.Path, "src", projectName);
        var source = XDocument.Load(Path.Combine(RepoRoot.Path, "Directory.Build.targets"));
        var configuration = XDocument.Load(Path.Combine(projectDirectory, $"{projectName}.csproj"));
        var neutralResources = Directory.GetFiles(Path.Combine(projectDirectory, resourceDirectory), "*.resx")
            .Select(Path.GetFileName)
            .Append("NewResource.resx")
            .Select(name => Path.Combine(resourceDirectory, name!))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var otherResources = new[]
        {
            Path.Combine(resourceDirectory, "NewResource.fr.resx"),
            Path.Combine(resourceDirectory, "NewResource.zh-Hans.resx"),
            Path.Combine("Other", "Unrelated.resx"),
            Path.Combine(resourceDirectory, "xlf", "Unrelated.resx"),
            Path.Combine("..", "Shared", "Linked.resx")
        };
        var designers = neutralResources.Select(path => Path.ChangeExtension(path, ".Designer.cs")).ToArray();
        var otherSources = new[]
        {
            Path.Combine(resourceDirectory, "Unrelated.cs"),
            Path.Combine("Other", "Unrelated.Designer.cs"),
            Path.Combine(resourceDirectory, "Nested", "Unrelated.Designer.cs"),
            Path.Combine("..", "Shared", "Linked.Designer.cs")
        };

        // Evaluate the real metadata rules against explicit items, including files that
        // are not in either project yet, without modifying the shared repository.
        var project = new XDocument(new XElement("Project",
            new XElement("PropertyGroup", configuration.Root!.Elements("PropertyGroup").Elements("ResxCodeGenerator").Select(property => new XElement(property))),
            new XElement("PropertyGroup", source.Root!.Elements("PropertyGroup").Elements("ResxCodeGenerator").Select(property => new XElement(property))),
            new XElement("ItemGroup",
                neutralResources.Concat(otherResources).Select(path => new XElement("EmbeddedResource", new XAttribute("Include", path))),
                designers.Concat(otherSources).Select(path => new XElement("Compile", new XAttribute("Include", path)))),
            new XElement("ItemGroup", source.Root.Elements("ItemGroup").Elements()
                .Where(item => item.Element("Generator") is not null || item.Element("AutoGen") is not null)
                .Select(item => new XElement(item)))));
        var linkedResource = project.Root!.Elements("ItemGroup").Elements("EmbeddedResource")
            .Single(item => item.Attribute("Include")?.Value == otherResources[^1]);
        linkedResource.Add(new XElement("Link", Path.Combine(resourceDirectory, "Linked.resx")));
        linkedResource.Add(new XElement("LogicalName", "Shared.Linked.resources"));
        var projectPath = Path.Combine(workspace.Path, "Resources.proj");
        project.Save(projectPath);
        var script = Path.Combine(workspace.Path, "evaluate.ps1");
        await File.WriteAllTextAsync(script, """
            & $env:TEST_DOTNET msbuild $env:TEST_PROJECT -nologo -getItem:EmbeddedResource,Compile
            exit $LASTEXITCODE
            """);
        using var command = new PowerShellCommand(script, output)
            .WithWorkingDirectory(workspace.Path)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithEnvironmentVariable("TEST_DOTNET", Path.Combine(RepoRoot.Path, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            .WithEnvironmentVariable("TEST_PROJECT", projectPath)
            .WithEnvironmentVariable("MSBUILDTERMINALLOGGER", "false");
        var result = await command.ExecuteAsync();
        result.EnsureSuccessful();

        using var document = JsonDocument.Parse(result.Output);
        var items = document.RootElement.GetProperty("Items");
        var resources = items.GetProperty("EmbeddedResource").EnumerateArray().ToArray();
        Assert.Equal(neutralResources.Concat(otherResources), resources.Select(item => item.GetProperty("Identity").GetString()));
        foreach (var resource in resources.Take(neutralResources.Length))
        {
            var name = Path.GetFileNameWithoutExtension(resource.GetProperty("Identity").GetString()!);
            Assert.Equal(generator, resource.GetProperty("Generator").GetString());
            Assert.Equal($"{name}.Designer.cs", resource.GetProperty("LastGenOutput").GetString());
            Assert.Equal("Designer", resource.GetProperty("SubType").GetString());
            Assert.Equal("Resx", resource.GetProperty("XlfSourceFormat").GetString());
            Assert.Equal("EmbeddedResource", resource.GetProperty("XlfOutputItem").GetString());
        }
        foreach (var resource in resources.Skip(neutralResources.Length))
        {
            Assert.False(resource.TryGetProperty("Generator", out _));
            Assert.False(resource.TryGetProperty("LastGenOutput", out _));
            Assert.False(resource.TryGetProperty("SubType", out _));
        }
        Assert.Equal(Path.Combine(resourceDirectory, "Linked.resx"), resources[^1].GetProperty("Link").GetString());
        Assert.Equal("Shared.Linked.resources", resources[^1].GetProperty("LogicalName").GetString());

        var compileItems = items.GetProperty("Compile").EnumerateArray().ToArray();
        Assert.Equal(designers.Concat(otherSources), compileItems.Select(item => item.GetProperty("Identity").GetString()));
        foreach (var designer in compileItems.Take(designers.Length))
        {
            var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(designer.GetProperty("Identity").GetString()!));
            Assert.Equal($"{name}.resx", designer.GetProperty("DependentUpon").GetString());
            Assert.Equal("True", designer.GetProperty("DesignTime").GetString());
            Assert.Equal("True", designer.GetProperty("AutoGen").GetString());
        }
        foreach (var sourceItem in compileItems.Skip(designers.Length))
        {
            Assert.False(sourceItem.TryGetProperty("DependentUpon", out _));
            Assert.False(sourceItem.TryGetProperty("DesignTime", out _));
            Assert.False(sourceItem.TryGetProperty("AutoGen", out _));
        }
    }
}
