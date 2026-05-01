// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Managed.NuGet.Commands;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class LayoutCommandTests
{
    [Fact]
    public async Task LayoutCommand_PrefersRuntimeTargetForCurrentRuntime_AndPreservesStructuredAssets()
    {
        var workspaceRoot = Directory.CreateTempSubdirectory("aspire-layout-tests").FullName;

        try
        {
            var packageRoot = Path.Combine(workspaceRoot, "packages", "test.package", "1.0.0");
            Directory.CreateDirectory(Path.Combine(packageRoot, "lib", "net10.0", "fr"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "runtimes", "unix", "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "runtimes", "win", "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "runtimes", GetCurrentRuntimeIdentifier(), "native"));

            await File.WriteAllTextAsync(Path.Combine(packageRoot, "lib", "net10.0", "Test.Package.dll"), "base");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "runtimes", "unix", "lib", "net10.0", "Test.Package.dll"), "unix");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "runtimes", "win", "lib", "net10.0", "Test.Package.dll"), "win");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "lib", "net10.0", "fr", "Test.Package.resources.dll"), "fr");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "runtimes", GetCurrentRuntimeIdentifier(), "native", GetNativeFileName()), "runtime-native");

            var assetsPath = Path.Combine(workspaceRoot, "project.assets.json");
            await File.WriteAllTextAsync(assetsPath, CreateAssetsJsonWithResolvedRidTarget(workspaceRoot, GetCurrentRuntimeIdentifier(), GetNativeFileName()));

            var outputPath = Path.Combine(workspaceRoot, "out");
            var command = LayoutCommand.Create();
            var parseResult = command.Parse([
                "--assets", assetsPath,
                "--output", outputPath,
                "--framework", "net10.0",
                "--runtime-identifier", GetCurrentRuntimeIdentifier()
            ]);

            var exitCode = await parseResult.InvokeAsync();

            Assert.Equal(0, exitCode);
            Assert.Equal(GetExpectedRuntimeContent(), await File.ReadAllTextAsync(Path.Combine(outputPath, "Test.Package.dll")));
            Assert.Equal("fr", await File.ReadAllTextAsync(Path.Combine(outputPath, "fr", "Test.Package.resources.dll")));
            Assert.Equal("runtime-native", await File.ReadAllTextAsync(Path.Combine(outputPath, GetNativeFileName())));
            Assert.Equal(
                GetExpectedRuntimeContent(),
                await File.ReadAllTextAsync(Path.Combine(outputPath, "runtimes", GetExpectedRuntimeAssetRid(), "lib", "net10.0", "Test.Package.dll")));
            Assert.Equal(
                "runtime-native",
                await File.ReadAllTextAsync(Path.Combine(outputPath, "runtimes", GetCurrentRuntimeIdentifier(), "native", GetNativeFileName())));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LayoutCommand_PrefersRuntimeSpecificTargetWhenMultipleTargetsExist()
    {
        var workspaceRoot = Directory.CreateTempSubdirectory("aspire-layout-tests").FullName;

        try
        {
            var packageRoot = Path.Combine(workspaceRoot, "packages", "test.package", "1.0.0");
            Directory.CreateDirectory(Path.Combine(packageRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "runtimes", "unix", "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "runtimes", "win", "lib", "net10.0"));

            await File.WriteAllTextAsync(Path.Combine(packageRoot, "lib", "net10.0", "Test.Package.dll"), "base");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "runtimes", "unix", "lib", "net10.0", "Test.Package.dll"), "unix");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "runtimes", "win", "lib", "net10.0", "Test.Package.dll"), "win");

            var assetsPath = Path.Combine(workspaceRoot, "project.assets.json");
            await File.WriteAllTextAsync(assetsPath, CreateAssetsJsonWithRuntimeSpecificTarget(workspaceRoot, GetCurrentRuntimeIdentifier(), GetExpectedRuntimeAssemblyPath()));

            var outputPath = Path.Combine(workspaceRoot, "out");
            var command = LayoutCommand.Create();
            var parseResult = command.Parse([
                "--assets", assetsPath,
                "--output", outputPath,
                "--framework", "net10.0",
                "--runtime-identifier", GetCurrentRuntimeIdentifier()
            ]);

            var exitCode = await parseResult.InvokeAsync();

            Assert.Equal(0, exitCode);
            Assert.Equal(GetExpectedRuntimeContent(), await File.ReadAllTextAsync(Path.Combine(outputPath, "Test.Package.dll")));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LayoutCommand_FallsBackToFrameworkTargetWhenRidTargetIsMissing()
    {
        var workspaceRoot = Directory.CreateTempSubdirectory("aspire-layout-tests").FullName;

        try
        {
            var packageRoot = Path.Combine(workspaceRoot, "packages", "test.package", "1.0.0");
            Directory.CreateDirectory(Path.Combine(packageRoot, "lib", "net10.0"));

            await File.WriteAllTextAsync(Path.Combine(packageRoot, "lib", "net10.0", "Test.Package.dll"), "base");

            var assetsPath = Path.Combine(workspaceRoot, "project.assets.json");
            await File.WriteAllTextAsync(assetsPath, CreateAssetsJsonWithBaseTargetOnly(workspaceRoot));

            var outputPath = Path.Combine(workspaceRoot, "out");
            var command = LayoutCommand.Create();
            var parseResult = command.Parse([
                "--assets", assetsPath,
                "--output", outputPath,
                "--framework", "net10.0",
                "--runtime-identifier", "made-up-rid"
            ]);

            var exitCode = await parseResult.InvokeAsync();

            Assert.Equal(0, exitCode);
            Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(outputPath, "Test.Package.dll")));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ManifestCommand_WritesPackageProbeManifestWithoutCreatingLibsLayout()
    {
        var workspaceRoot = Directory.CreateTempSubdirectory("aspire-manifest-tests").FullName;

        try
        {
            var packageRoot = Path.Combine(workspaceRoot, "packages", "test.package", "1.0.0");
            Directory.CreateDirectory(Path.Combine(packageRoot, "lib", "net10.0", "fr"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "runtimes", "unix", "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "runtimes", "win", "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "runtimes", GetCurrentRuntimeIdentifier(), "native"));

            await File.WriteAllTextAsync(Path.Combine(packageRoot, "lib", "net10.0", "Test.Package.dll"), "base");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "runtimes", "unix", "lib", "net10.0", "Test.Package.dll"), "unix");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "runtimes", "win", "lib", "net10.0", "Test.Package.dll"), "win");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "lib", "net10.0", "fr", "Test.Package.resources.dll"), "fr");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "runtimes", GetCurrentRuntimeIdentifier(), "native", GetNativeFileName()), "runtime-native");

            var assetsPath = Path.Combine(workspaceRoot, "project.assets.json");
            await File.WriteAllTextAsync(assetsPath, CreateAssetsJsonWithResolvedRidTarget(workspaceRoot, GetCurrentRuntimeIdentifier(), GetNativeFileName()));

            var outputPath = Path.Combine(workspaceRoot, "integration-package-probe-manifest.json");
            var command = ManifestCommand.Create();
            var parseResult = command.Parse([
                "--assets", assetsPath,
                "--output", outputPath,
                "--framework", "net10.0",
                "--runtime-identifier", GetCurrentRuntimeIdentifier()
            ]);

            var exitCode = await parseResult.InvokeAsync();

            Assert.Equal(0, exitCode);
            Assert.False(Directory.Exists(Path.Combine(workspaceRoot, "libs")));

            await using var manifestStream = File.OpenRead(outputPath);
            using var manifest = await JsonDocument.ParseAsync(manifestStream);

            var managedAssemblies = manifest.RootElement.GetProperty("managedAssemblies").EnumerateArray().ToList();
            Assert.Contains(
                managedAssemblies,
                assembly => assembly.GetProperty("name").GetString() == "Test.Package" &&
                    assembly.GetProperty("path").GetString() == Path.Combine(packageRoot, GetExpectedRuntimeAssemblyPath().Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains(
                managedAssemblies,
                assembly => assembly.GetProperty("name").GetString() == "Test.Package.resources" &&
                    assembly.GetProperty("culture").GetString() == "fr" &&
                    assembly.GetProperty("path").GetString() == Path.Combine(packageRoot, "lib", "net10.0", "fr", "Test.Package.resources.dll"));

            var nativeLibraries = manifest.RootElement.GetProperty("nativeLibraries").EnumerateArray().ToList();
            Assert.Contains(
                nativeLibraries,
                nativeLibrary => nativeLibrary.GetProperty("fileName").GetString() == GetNativeFileName() &&
                    nativeLibrary.GetProperty("path").GetString() == Path.Combine(packageRoot, "runtimes", GetCurrentRuntimeIdentifier(), "native", GetNativeFileName()));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    private static string CreateAssetsJsonWithResolvedRidTarget(string rootPath, string runtimeIdentifier, string nativeFileName)
    {
        var packagesPath = Path.Combine(rootPath, "packages") + Path.DirectorySeparatorChar;
        var escapedPackagesPath = packagesPath.Replace("\\", "\\\\");
        var outputPath = Path.Combine(rootPath, "obj") + Path.DirectorySeparatorChar;
        var escapedOutputPath = outputPath.Replace("\\", "\\\\");
        var runtimeAssemblyPath = GetExpectedRuntimeAssemblyPath();
        var runtimeTargetRid = GetExpectedRuntimeAssetRid();

        return $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Test.Package/1.0.0": {
                    "type": "package",
                    "runtime": {
                      "lib/net10.0/Test.Package.dll": {}
                    },
                    "resource": {
                      "lib/net10.0/fr/Test.Package.resources.dll": { "locale": "fr" }
                    },
                    "runtimeTargets": {
                      "{{runtimeAssemblyPath}}": { "rid": "{{runtimeTargetRid}}", "assetType": "runtime" },
                      "runtimes/{{runtimeIdentifier}}/native/{{nativeFileName}}": { "rid": "{{runtimeIdentifier}}", "assetType": "native" }
                    }
                  }
                },
                "net10.0/{{runtimeIdentifier}}": {
                  "Test.Package/1.0.0": {
                    "type": "package",
                    "runtime": {
                      "{{runtimeAssemblyPath}}": {}
                    },
                    "native": {
                      "runtimes/{{runtimeIdentifier}}/native/{{nativeFileName}}": {}
                    },
                    "resource": {
                      "lib/net10.0/fr/Test.Package.resources.dll": { "locale": "fr" }
                    }
                  }
                }
              },
              "libraries": {
                "Test.Package/1.0.0": {
                  "type": "package",
                  "path": "test.package/1.0.0",
                  "files": [
                    "lib/net10.0/Test.Package.dll",
                    "{{runtimeAssemblyPath}}",
                    "runtimes/{{runtimeIdentifier}}/native/{{nativeFileName}}",
                    "lib/net10.0/fr/Test.Package.resources.dll"
                  ]
                }
              },
              "packageFolders": {
                "{{escapedPackagesPath}}": {}
              },
              "project": {
                "version": "1.0.0",
                "restore": {
                  "projectUniqueName": "test",
                  "projectName": "test",
                  "projectPath": "test",
                  "packagesPath": "{{escapedPackagesPath}}",
                  "outputPath": "{{escapedOutputPath}}",
                  "projectStyle": "PackageReference",
                  "originalTargetFrameworks": [ "net10.0" ],
                  "sources": {},
                  "frameworks": {
                    "net10.0": {
                      "targetAlias": "net10.0",
                      "projectReferences": {}
                    }
                  }
                },
                "frameworks": {
                  "net10.0": {
                    "targetAlias": "net10.0",
                    "dependencies": {}
                  }
                }
              }
            }
            """;
    }

    private static string CreateAssetsJsonWithRuntimeSpecificTarget(string rootPath, string runtimeIdentifier, string runtimeAssemblyPath)
    {
        var packagesPath = Path.Combine(rootPath, "packages") + Path.DirectorySeparatorChar;
        var escapedPackagesPath = packagesPath.Replace("\\", "\\\\");
        var outputPath = Path.Combine(rootPath, "obj") + Path.DirectorySeparatorChar;
        var escapedOutputPath = outputPath.Replace("\\", "\\\\");

        return $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Test.Package/1.0.0": {
                    "type": "package",
                    "runtime": {
                      "lib/net10.0/Test.Package.dll": {}
                    }
                  }
                },
                "net10.0/{{runtimeIdentifier}}": {
                  "Test.Package/1.0.0": {
                    "type": "package",
                    "runtime": {
                      "{{runtimeAssemblyPath}}": {}
                    }
                  }
                }
              },
              "libraries": {
                "Test.Package/1.0.0": {
                  "type": "package",
                  "path": "test.package/1.0.0",
                  "files": [
                    "lib/net10.0/Test.Package.dll",
                    "{{runtimeAssemblyPath}}"
                  ]
                }
              },
              "packageFolders": {
                "{{escapedPackagesPath}}": {}
              },
              "project": {
                "version": "1.0.0",
                "restore": {
                  "projectUniqueName": "test",
                  "projectName": "test",
                  "projectPath": "test",
                  "packagesPath": "{{escapedPackagesPath}}",
                  "outputPath": "{{escapedOutputPath}}",
                  "projectStyle": "PackageReference",
                  "originalTargetFrameworks": [ "net10.0" ],
                  "sources": {},
                  "frameworks": {
                    "net10.0": {
                      "targetAlias": "net10.0",
                      "projectReferences": {}
                    }
                  }
                },
                "frameworks": {
                  "net10.0": {
                    "targetAlias": "net10.0",
                    "dependencies": {}
                  }
                }
              }
            }
            """;
    }

    private static string CreateAssetsJsonWithBaseTargetOnly(string rootPath)
    {
        var packagesPath = Path.Combine(rootPath, "packages") + Path.DirectorySeparatorChar;
        var escapedPackagesPath = packagesPath.Replace("\\", "\\\\");
        var outputPath = Path.Combine(rootPath, "obj") + Path.DirectorySeparatorChar;
        var escapedOutputPath = outputPath.Replace("\\", "\\\\");

        return $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Test.Package/1.0.0": {
                    "type": "package",
                    "runtime": {
                      "lib/net10.0/Test.Package.dll": {}
                    }
                  }
                }
              },
              "libraries": {
                "Test.Package/1.0.0": {
                  "type": "package",
                  "path": "test.package/1.0.0",
                  "files": [
                    "lib/net10.0/Test.Package.dll"
                  ]
                }
              },
              "packageFolders": {
                "{{escapedPackagesPath}}": {}
              },
              "project": {
                "version": "1.0.0",
                "restore": {
                  "projectUniqueName": "test",
                  "projectName": "test",
                  "projectPath": "test",
                  "packagesPath": "{{escapedPackagesPath}}",
                  "outputPath": "{{escapedOutputPath}}",
                  "projectStyle": "PackageReference",
                  "originalTargetFrameworks": [ "net10.0" ],
                  "sources": {},
                  "frameworks": {
                    "net10.0": {
                      "targetAlias": "net10.0",
                      "projectReferences": {}
                    }
                  }
                },
                "frameworks": {
                  "net10.0": {
                    "targetAlias": "net10.0",
                    "dependencies": {}
                  }
                }
              }
            }
            """;
    }

    private static string GetCurrentRuntimeIdentifier() => RuntimeInformation.RuntimeIdentifier;

    private static string GetExpectedRuntimeAssemblyPath() => OperatingSystem.IsWindows()
        ? "runtimes/win/lib/net10.0/Test.Package.dll"
        : "runtimes/unix/lib/net10.0/Test.Package.dll";

    private static string GetExpectedRuntimeAssetRid() => OperatingSystem.IsWindows() ? "win" : "unix";

    private static string GetExpectedRuntimeContent() => OperatingSystem.IsWindows() ? "win" : "unix";

    private static string GetNativeFileName() => OperatingSystem.IsWindows() ? "TestNative.dll" : OperatingSystem.IsMacOS() ? "libTestNative.dylib" : "libTestNative.so";
}
