// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Managed.NuGet.Commands;
using System.Runtime.InteropServices;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class LayoutCommandTests
{
    [Fact]
    public async Task LayoutCommand_PrefersRuntimeTargetForCurrentRuntime_AndPreservesStructuredAssets()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workspaceRoot);

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
            Directory.CreateDirectory(Path.Combine(packageRoot, "native"));
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "native", GetNativeFileName()), "generic-native");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "runtimes", GetCurrentRuntimeIdentifier(), "native", GetNativeFileName()), "runtime-native");

            var assetsPath = Path.Combine(workspaceRoot, "project.assets.json");
            await File.WriteAllTextAsync(assetsPath, CreateAssetsJson(workspaceRoot, GetCurrentRuntimeIdentifier(), GetNativeFileName()));

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
            Assert.Equal("generic-native", await File.ReadAllTextAsync(Path.Combine(outputPath, "native", GetNativeFileName())));
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
        var workspaceRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workspaceRoot);

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
            await File.WriteAllTextAsync(assetsPath, CreateAssetsJsonWithRuntimeSpecificTarget(workspaceRoot, GetCurrentRuntimeIdentifier()));

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

    private static string CreateAssetsJson(string rootPath, string runtimeIdentifier, string nativeFileName)
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
                    },
                    "native": {
                      "native/{{nativeFileName}}": {}
                    },
                    "runtimeTargets": {
                      "runtimes/unix/lib/net10.0/Test.Package.dll": { "rid": "unix", "assetType": "runtime" },
                      "runtimes/win/lib/net10.0/Test.Package.dll": { "rid": "win", "assetType": "runtime" },
                      "runtimes/{{runtimeIdentifier}}/native/{{nativeFileName}}": { "rid": "{{runtimeIdentifier}}", "assetType": "native" }
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
                    "native/{{nativeFileName}}",
                    "runtimes/unix/lib/net10.0/Test.Package.dll",
                    "runtimes/win/lib/net10.0/Test.Package.dll",
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

    private static string CreateAssetsJsonWithRuntimeSpecificTarget(string rootPath, string runtimeIdentifier)
    {
        var packagesPath = Path.Combine(rootPath, "packages") + Path.DirectorySeparatorChar;
        var escapedPackagesPath = packagesPath.Replace("\\", "\\\\");
        var outputPath = Path.Combine(rootPath, "obj") + Path.DirectorySeparatorChar;
        var escapedOutputPath = outputPath.Replace("\\", "\\\\");
        var runtimeAssemblyPath = GetExpectedRuntimeAssemblyPath();

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
                    "runtimes/unix/lib/net10.0/Test.Package.dll",
                    "runtimes/win/lib/net10.0/Test.Package.dll"
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

    [Fact]
    public async Task LayoutCommand_UsesRequestedRuntimeIdentifierForPortableAliasFallbacks()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            var packageRoot = Path.Combine(workspaceRoot, "packages", "test.package", "1.0.0");
            Directory.CreateDirectory(Path.Combine(packageRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "runtimes", "unix", "lib", "net10.0"));

            await File.WriteAllTextAsync(Path.Combine(packageRoot, "lib", "net10.0", "Test.Package.dll"), "base");
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "runtimes", "unix", "lib", "net10.0", "Test.Package.dll"), "unix");

            var assetsPath = Path.Combine(workspaceRoot, "project.assets.json");
            await File.WriteAllTextAsync(assetsPath, CreateAssetsJsonWithRuntimeTargetOnly(workspaceRoot));

            var outputPath = Path.Combine(workspaceRoot, "out");
            var command = LayoutCommand.Create();
            var parseResult = command.Parse([
                "--assets", assetsPath,
                "--output", outputPath,
                "--framework", "net10.0",
                "--runtime-identifier", "osx-arm64"
            ]);

            var exitCode = await parseResult.InvokeAsync();

            Assert.Equal(0, exitCode);
            Assert.Equal("unix", await File.ReadAllTextAsync(Path.Combine(outputPath, "Test.Package.dll")));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    private static string CreateAssetsJsonWithRuntimeTargetOnly(string rootPath)
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
                    },
                    "runtimeTargets": {
                      "runtimes/unix/lib/net10.0/Test.Package.dll": { "rid": "unix", "assetType": "runtime" }
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
                    "runtimes/unix/lib/net10.0/Test.Package.dll"
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
