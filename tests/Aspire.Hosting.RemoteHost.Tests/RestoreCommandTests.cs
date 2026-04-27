// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aspire.Managed.NuGet.Commands;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class RestoreCommandTests
{
    [Fact]
    public async Task RestoreCommand_UsesRuntimeIdentifierGraphToResolvePortableRuntimeAssets()
    {
        var workspaceRoot = Directory.CreateTempSubdirectory("aspire-restore-tests").FullName;

        try
        {
            var sourcePath = Path.Combine(workspaceRoot, "source");
            var outputPath = Path.Combine(workspaceRoot, "obj");

            Directory.CreateDirectory(sourcePath);
            Directory.CreateDirectory(outputPath);

            CreatePackage(sourcePath);

            var command = RestoreCommand.Create();
            var parseResult = command.Parse([
                "--package", "Test.Package,1.0.0",
                "--framework", "net10.0",
                "--runtime-identifier", RuntimeInformation.RuntimeIdentifier,
                "--output", outputPath,
                "--source", sourcePath,
                "--no-nuget-org"
            ]);

            var exitCode = await parseResult.InvokeAsync();

            Assert.Equal(0, exitCode);

            var assetsPath = Path.Combine(outputPath, "project.assets.json");
            using var stream = File.OpenRead(assetsPath);
            using var document = await JsonDocument.ParseAsync(stream);

            var targets = document.RootElement.GetProperty("targets");
            Assert.True(targets.TryGetProperty($"net10.0/{RuntimeInformation.RuntimeIdentifier}", out var ridTarget));

            var runtimeEntries = ridTarget
                .GetProperty("Test.Package/1.0.0")
                .GetProperty("runtime")
                .EnumerateObject()
                .Select(p => p.Name)
                .ToArray();

            Assert.Equal([GetExpectedRuntimeAssemblyPath()], runtimeEntries);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    private static void CreatePackage(string sourcePath)
    {
        var packagePath = Path.Combine(sourcePath, "Test.Package.1.0.0.nupkg");

        using var fileStream = File.Create(packagePath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        AddEntry(
            archive,
            "Test.Package.nuspec",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Test.Package</id>
                <version>1.0.0</version>
                <authors>Aspire</authors>
                <description>Test package</description>
              </metadata>
            </package>
            """);

        AddEntry(archive, "lib/net10.0/Test.Package.dll", "base");
        AddEntry(archive, "runtimes/unix/lib/net10.0/Test.Package.dll", "unix");
        AddEntry(archive, "runtimes/win/lib/net10.0/Test.Package.dll", "win");
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string GetExpectedRuntimeAssemblyPath() => OperatingSystem.IsWindows()
        ? "runtimes/win/lib/net10.0/Test.Package.dll"
        : "runtimes/unix/lib/net10.0/Test.Package.dll";
}
