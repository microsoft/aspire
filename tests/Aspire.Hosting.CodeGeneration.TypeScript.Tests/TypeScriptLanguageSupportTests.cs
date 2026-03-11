// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Ats;

namespace Aspire.Hosting.CodeGeneration.TypeScript.Tests;

public sealed class TypeScriptLanguageSupportTests
{
    private readonly TypeScriptLanguageSupport _languageSupport = new();

    [Fact]
    public void Scaffold_CreatesAppHostSpecificScriptsAndTsConfig_ForNewProject()
    {
        using var testDirectory = new TestDirectory();

        var files = _languageSupport.Scaffold(new ScaffoldRequest
        {
            TargetPath = testDirectory.Path,
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
        Assert.Equal("module", packageJson["type"]?.GetValue<string>());
        Assert.Equal("aspire run", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("tsc --watch -p tsconfig.apphost.json", scripts["aspire:dev"]?.GetValue<string>());
        Assert.False(scripts.ContainsKey("start"));
        Assert.False(scripts.ContainsKey("build"));
        Assert.False(scripts.ContainsKey("dev"));
        Assert.Equal("^4.19.0", devDependencies["tsx"]?.GetValue<string>());
        Assert.Equal("^5.3.0", devDependencies["typescript"]?.GetValue<string>());

        var tsConfig = ParseJson(files["tsconfig.apphost.json"]);
        Assert.Equal("./dist/apphost", tsConfig["compilerOptions"]?["outDir"]?.GetValue<string>());
    }

    [Fact]
    public void Scaffold_MergesExistingPackageJson_WithoutOverwritingExistingAppValues()
    {
        using var testDirectory = new TestDirectory();

        File.WriteAllText(Path.Combine(testDirectory.Path, "package.json"), """
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
            TargetPath = testDirectory.Path,
            ProjectName = "Ignored"
        });

        var packageJson = ParseJson(files["package.json"]);
        var scripts = packageJson["scripts"]!.AsObject();
        var dependencies = packageJson["dependencies"]!.AsObject();
        var devDependencies = packageJson["devDependencies"]!.AsObject();

        Assert.Equal("vite-brownfield", packageJson["name"]?.GetValue<string>());
        Assert.Equal("2.0.0", packageJson["version"]?.GetValue<string>());
        Assert.Null(packageJson["type"]);
        Assert.Equal("vite", scripts["dev"]?.GetValue<string>());
        Assert.Equal("vite build", scripts["build"]?.GetValue<string>());
        Assert.Equal("vite preview", scripts["preview"]?.GetValue<string>());
        Assert.Equal("aspire run", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("tsc --watch -p tsconfig.apphost.json", scripts["aspire:dev"]?.GetValue<string>());
        Assert.Equal("^9.9.9", dependencies["vscode-jsonrpc"]?.GetValue<string>());
        Assert.Equal("^9.9.9", devDependencies["tsx"]?.GetValue<string>());
        Assert.Equal("^7.0.0", devDependencies["vite"]?.GetValue<string>());
        Assert.Equal("^20.0.0", devDependencies["@types/node"]?.GetValue<string>());
        Assert.Equal("^3.1.11", devDependencies["nodemon"]?.GetValue<string>());
        Assert.Equal("^5.3.0", devDependencies["typescript"]?.GetValue<string>());
    }

    [Fact]
    public void Scaffold_DoesNotEmitRootTsConfig_WhenOneAlreadyExists()
    {
        using var testDirectory = new TestDirectory();
        var existingTsConfigPath = Path.Combine(testDirectory.Path, "tsconfig.json");
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
            TargetPath = testDirectory.Path,
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

        Assert.Equal(new[] { "tsx", "--tsconfig", "tsconfig.apphost.json", "{appHostFile}" }, runtimeSpec.Execute.Args);
        Assert.Contains("npx tsx --tsconfig tsconfig.apphost.json {appHostFile}", watchExecute.Args);
    }

    private static JsonObject ParseJson(string content) => JsonNode.Parse(content)!.AsObject();

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aspire-ts-language-support-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
