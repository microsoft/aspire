// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Xml.Linq;
using GenerateTemplateManifest;
using Xunit;

namespace Infrastructure.Tests.TemplateManifest;

internal sealed class TemplateManifestTestEnvironment
{
    private readonly TemporaryWorkspace _workspace;
    private readonly ITestOutputHelper _output;

    internal string SourceDirectory { get; }
    internal string TemplateDirectory { get; }
    internal string BuiltFeed { get; }
    internal string ExternalFeed { get; }
    internal string SharedCache { get; }
    internal string RestoreDirectory { get; }
    internal string ManifestPath { get; }
    internal string ConfigPath { get; }

    internal TemplateManifestTestEnvironment(TemporaryWorkspace workspace, ITestOutputHelper output)
    {
        _workspace = workspace;
        _output = output;
        SourceDirectory = workspace.CreateDirectory("source").FullName;
        TemplateDirectory = Directory.CreateDirectory(Path.Combine(SourceDirectory, "template")).FullName;
        BuiltFeed = workspace.CreateDirectory("built feed").FullName;
        ExternalFeed = workspace.CreateDirectory("external feed").FullName;
        SharedCache = workspace.CreateDirectory("shared cache").FullName;
        RestoreDirectory = Path.Combine(workspace.Path, "restore");
        ManifestPath = Path.Combine(workspace.Path, "cgmanifest.json");
        ConfigPath = Path.Combine(workspace.Path, "nuget.config");

        Directory.CreateDirectory(Path.Combine(TemplateDirectory, ".template.config"));
        File.WriteAllText(Path.Combine(TemplateDirectory, ".template.config", "template.json"), """
            {
              "identity": "manifest-fixture", "name": "Manifest fixture", "shortName": "manifest-fixture",
              "symbols": {
                "Framework": {
                  "type": "parameter", "datatype": "choice", "defaultValue": "net11.0",
                  "choices": [{"choice": "net11.0"}]
                }
              }
            }
            """);
        new XDocument(new XElement("configuration",
            new XElement("packageSources",
                new XElement("clear"),
                new XElement("add", new XAttribute("key", "external"), new XAttribute("value", ExternalFeed))),
            new XElement("packageSourceMapping",
                new XElement("clear"),
                new XElement("packageSource", new XAttribute("key", "external"),
                    new XElement("package", new XAttribute("pattern", "*")))),
            new XElement("fallbackPackageFolders", new XElement("clear")))).Save(ConfigPath);
    }

    internal PowerShellCommand CreateCommand()
    {
        return CreateDotnetCommand("generate.ps1", """
            & $env:TEST_DOTNET $env:TEST_GENERATOR $env:TEST_SOURCE $env:TEST_SOURCE `
                $env:TEST_MANIFEST $env:TEST_CONFIG $env:TEST_FEED $env:TEST_RESTORE
            exit $LASTEXITCODE
            """)
            .WithEnvironmentVariable("TEST_GENERATOR", typeof(TemplateRestorePlan).Assembly.Location)
            .WithEnvironmentVariable("TEST_SOURCE", SourceDirectory)
            .WithEnvironmentVariable("TEST_MANIFEST", ManifestPath)
            .WithEnvironmentVariable("TEST_FEED", BuiltFeed)
            .WithEnvironmentVariable("TEST_RESTORE", RestoreDirectory);
    }

    internal PowerShellCommand CreateRestoreCommand(string projectPath)
    {
        return CreateDotnetCommand("restore.ps1", """
            & $env:TEST_DOTNET restore $env:TEST_PROJECT --configfile $env:TEST_CONFIG `
                --disable-build-servers --verbosity quiet -p:NuGetAudit=false
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            & $env:TEST_DOTNET msbuild $env:TEST_PROJECT -nologo -getItem:PackageReference
            exit $LASTEXITCODE
            """)
            .WithEnvironmentVariable("TEST_PROJECT", projectPath);
    }

    private PowerShellCommand CreateDotnetCommand(string scriptName, string scriptContent)
    {
        // Use the installed repository SDK and isolate all feeds, HTTP caches and extracted
        // packages. The generated projects target net11.0 so no targeting-pack download is needed.
        var dotnet = Path.Combine(RepoRoot.Path, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Assert.True(File.Exists(dotnet), "The repository SDK must be initialized before running infrastructure tests.");
        var script = Path.Combine(_workspace.Path, scriptName);
        File.WriteAllText(script, scriptContent);

        return new PowerShellCommand(script, _output)
            .WithWorkingDirectory(_workspace.Path)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithEnvironmentVariable("TEST_DOTNET", dotnet)
            .WithEnvironmentVariable("PATH", Path.GetDirectoryName(dotnet) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"))
            .WithEnvironmentVariable("TEST_CONFIG", ConfigPath)
            .WithEnvironmentVariable("NUGET_PACKAGES", SharedCache)
            .WithEnvironmentVariable("NUGET_HTTP_CACHE_PATH", Path.Combine(_workspace.Path, "http-cache"))
            .WithEnvironmentVariable("DOTNET_CLI_HOME", _workspace.Path)
            .WithEnvironmentVariable("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1")
            .WithEnvironmentVariable("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false")
            .WithEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
            .WithEnvironmentVariable("DOTNET_NOLOGO", "1")
            .WithEnvironmentVariable("MSBUILDTERMINALLOGGER", "false");
    }

    internal static string CreatePackage(string feed, string name, string version, params (string Name, string Version)[] dependencies)
    {
        var path = Path.Combine(feed, $"{name}.{version}.nupkg");
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        using var stream = archive.CreateEntry($"{name}.nuspec").Open();
        new XDocument(new XElement("package",
            new XElement("metadata",
                new XElement("id", name),
                new XElement("version", version),
                new XElement("authors", "Test"),
                new XElement("description", "Hermetic template manifest fixture"),
                dependencies.Length == 0 ? null :
                    new XElement("dependencies", dependencies.Select(dependency =>
                        new XElement("dependency", new XAttribute("id", dependency.Name),
                            new XAttribute("version", dependency.Version))))))).Save(stream);
        return path;
    }
}
