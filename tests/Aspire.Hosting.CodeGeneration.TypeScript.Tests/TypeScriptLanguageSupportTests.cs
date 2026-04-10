// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public sealed class TypeScriptLanguageSupportTests
{
    private readonly TypeScriptLanguageSupport _languageSupport = new();

    [Fact]
    public void Scaffold_CreatesAppHostSpecificScriptsAndTsConfig_ForNewProject()
    {
        using var testDir = new TestTempDirectory();

        var files = _languageSupport.Scaffold(new ScaffoldRequest
        {
            TargetPath = testDir.Path,
            ProjectName = "BrownfieldApp"
        });

        Assert.Contains("apphost.ts", files.Keys);
        Assert.Contains("package.json", files.Keys);
        Assert.Contains("tsconfig.apphost.json", files.Keys);
        Assert.DoesNotContain("tsconfig.json", files.Keys);

        var packageJson = ParseJson(files["package.json"]);
        var scripts = packageJson["scripts"]!.AsObject();
        var devDependencies = packageJson["devDependencies"]!.AsObject();

        Assert.Equal("brownfieldapp", packageJson["name"]?.GetValue<string>());
        Assert.Equal("1.0.0", packageJson["version"]?.GetValue<string>());
        Assert.True(packageJson["private"]?.GetValue<bool>());
        Assert.Equal("module", packageJson["type"]?.GetValue<string>());
        Assert.Equal("aspire run", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("tsc --watch -p tsconfig.apphost.json", scripts["aspire:dev"]?.GetValue<string>());
        Assert.Equal("eslint apphost.ts", scripts["aspire:lint"]?.GetValue<string>());
        Assert.False(scripts.ContainsKey("start"));
        Assert.False(scripts.ContainsKey("build"));
        Assert.False(scripts.ContainsKey("dev"));
        Assert.Equal("^4.21.0", devDependencies["tsx"]?.GetValue<string>());
        Assert.Equal("^5.9.3", devDependencies["typescript"]?.GetValue<string>());
        Assert.Equal("^10.0.3", devDependencies["eslint"]?.GetValue<string>());
        Assert.Equal("^8.57.1", devDependencies["typescript-eslint"]?.GetValue<string>());

        var engines = packageJson["engines"]!.AsObject();
        Assert.Equal("^20.19.0 || ^22.13.0 || >=24", engines["node"]?.GetValue<string>());

        // Verify the raw JSON does not contain unicode escapes for >= (fidelity check)
        Assert.DoesNotContain("\\u003E", files["package.json"]);

        Assert.Contains("eslint.config.mjs", files.Keys);

        var tsConfig = ParseJson(files["tsconfig.apphost.json"]);
        Assert.Equal("./dist/apphost", tsConfig["compilerOptions"]?["outDir"]?.GetValue<string>());
    }

    [Fact]
    public void Scaffold_BrownfieldOutput_ContainsOnlyAspireEntries()
    {
        using var testDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(testDir.Path, "package.json"), """
            {
              "name": "vite-brownfield",
              "version": "2.0.0",
              "scripts": {
                "dev": "vite",
                "build": "vite build",
                "preview": "vite preview",
                "aspire:start": "custom-start"
              },
              "dependencies": {
                "vscode-jsonrpc": "^9.9.9"
              },
              "devDependencies": {
                "tsx": "^9.9.9",
                "vite": "^7.0.0"
              }
            }
            """);

        var files = _languageSupport.Scaffold(new ScaffoldRequest
        {
            TargetPath = testDir.Path,
            ProjectName = "Ignored"
        });

        var packageJson = ParseJson(files["package.json"]);
        var scripts = packageJson["scripts"]!.AsObject();
        var dependencies = packageJson["dependencies"]!.AsObject();
        var devDependencies = packageJson["devDependencies"]!.AsObject();

        // Scaffold output should NOT echo existing content — the CLI-side
        // PackageJsonMerger handles combining with the on-disk file.
        Assert.Null(packageJson["name"]);
        Assert.Null(packageJson["version"]);
        Assert.Null(packageJson["type"]);
        Assert.Null(packageJson["private"]);

        // Scaffold should only contain Aspire-desired scripts
        Assert.Equal("aspire run", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("tsc --watch -p tsconfig.apphost.json", scripts["aspire:dev"]?.GetValue<string>());
        Assert.Equal("eslint apphost.ts", scripts["aspire:lint"]?.GetValue<string>());
        Assert.False(scripts.ContainsKey("dev"));
        Assert.False(scripts.ContainsKey("build"));
        Assert.False(scripts.ContainsKey("preview"));

        // Scaffold should only contain Aspire-desired dependencies (at Aspire's versions)
        Assert.Equal("^8.2.0", dependencies["vscode-jsonrpc"]?.GetValue<string>());
        Assert.Equal("^4.21.0", devDependencies["tsx"]?.GetValue<string>());
        Assert.Equal("^22.0.0", devDependencies["@types/node"]?.GetValue<string>());
        Assert.Equal("^3.1.14", devDependencies["nodemon"]?.GetValue<string>());
        Assert.Equal("^5.9.3", devDependencies["typescript"]?.GetValue<string>());
        Assert.False(devDependencies.ContainsKey("vite"));

        // engines.node is always set
        var engines = packageJson["engines"]!.AsObject();
        Assert.Equal("^20.19.0 || ^22.13.0 || >=24", engines["node"]?.GetValue<string>());
    }

    [Fact]
    public void Scaffold_AlwaysOutputsAspireVersions_RegardlessOfExistingDependencies()
    {
        using var testDir = new TestTempDirectory();

        File.WriteAllText(Path.Combine(testDir.Path, "package.json"), """
            {
              "dependencies": {
                "vscode-jsonrpc": "^8.1.0"
              },
              "devDependencies": {
                "@types/node": "^18.0.0",
                "nodemon": "^3.1.0",
                "tsx": "^4.18.0",
                "typescript": "^5.2.0"
              }
            }
            """);

        var files = _languageSupport.Scaffold(new ScaffoldRequest
        {
            TargetPath = testDir.Path,
            ProjectName = "Ignored"
        });

        var packageJson = ParseJson(files["package.json"]);
        var dependencies = packageJson["dependencies"]!.AsObject();
        var devDependencies = packageJson["devDependencies"]!.AsObject();

        // Scaffold always produces Aspire's desired versions — the CLI-side
        // PackageJsonMerger handles semver comparison with existing on-disk versions.
        Assert.Equal("^8.2.0", dependencies["vscode-jsonrpc"]?.GetValue<string>());
        Assert.Equal("^22.0.0", devDependencies["@types/node"]?.GetValue<string>());
        Assert.Equal("^3.1.14", devDependencies["nodemon"]?.GetValue<string>());
        Assert.Equal("^4.21.0", devDependencies["tsx"]?.GetValue<string>());
        Assert.Equal("^5.9.3", devDependencies["typescript"]?.GetValue<string>());
    }

    [Fact]
    public void Scaffold_DoesNotEmitRootTsConfig_WhenOneAlreadyExists()
    {
        using var testDir = new TestTempDirectory();
        var existingTsConfigPath = Path.Combine(testDir.Path, "tsconfig.json");
        var existingTsConfig = """
            {
              "compilerOptions": {
                "module": "ESNext"
              }
            }
            """;

        File.WriteAllText(existingTsConfigPath, existingTsConfig);

        var files = _languageSupport.Scaffold(new ScaffoldRequest
        {
            TargetPath = testDir.Path,
            ProjectName = "BrownfieldApp"
        });

        Assert.DoesNotContain("tsconfig.json", files.Keys);
        Assert.Contains("tsconfig.apphost.json", files.Keys);
        Assert.Equal(existingTsConfig, File.ReadAllText(existingTsConfigPath));
    }

    [Fact]
    public void GetRuntimeSpec_UsesAppHostSpecificTsConfig()
    {
        var runtimeSpec = _languageSupport.GetRuntimeSpec();
        var watchExecute = Assert.IsType<CommandSpec>(runtimeSpec.WatchExecute);

        Assert.Equal(new[] { "--no-install", "tsx", "--tsconfig", "tsconfig.apphost.json", "{appHostFile}" }, runtimeSpec.Execute.Args);
        Assert.Contains("npx --no-install tsx --tsconfig tsconfig.apphost.json {appHostFile}", watchExecute.Args);
    }

    private static JsonObject ParseJson(string content) => JsonNode.Parse(content)!.AsObject();
}
