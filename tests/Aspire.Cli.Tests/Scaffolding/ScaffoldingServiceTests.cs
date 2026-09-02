// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Scaffolding;

public class ScaffoldingServiceTests(ITestOutputHelper outputHelper)
{
    private static readonly LanguageInfo s_typeScriptLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript (Node.js)",
        PackageName: "@aspire/app-host",
        DetectionPatterns: ["apphost.mts", "apphost.ts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.mts");

    private static readonly LanguageInfo s_pythonLanguage = new(
        LanguageId: new LanguageId("python"),
        DisplayName: "Python",
        PackageName: "aspire-app-host",
        DetectionPatterns: ["apphost.py"],
        CodeGenerator: "python",
        AppHostFileName: "apphost.py");

    [Fact]
    public void GetScaffoldDirectory_UsesNestedPackage_ForBrownfieldTypeScript()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            File.WriteAllText(Path.Combine(rootDirectory.FullName, "package.json"), "{}");

            var scaffoldDirectory = ScaffoldingService.GetScaffoldDirectory(rootDirectory, s_typeScriptLanguage);

            Assert.Equal(
                Path.Combine(rootDirectory.FullName, ScaffoldingService.BrownfieldTypeScriptAppHostDirectoryName),
                scaffoldDirectory.FullName);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetScaffoldDirectory_UsesRoot_ForGreenfieldTypeScript()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var scaffoldDirectory = ScaffoldingService.GetScaffoldDirectory(rootDirectory, s_typeScriptLanguage);

            Assert.Equal(rootDirectory.FullName, scaffoldDirectory.FullName);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetScaffoldDirectory_UsesRoot_ForNonTypeScript()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            File.WriteAllText(Path.Combine(rootDirectory.FullName, "package.json"), "{}");

            var scaffoldDirectory = ScaffoldingService.GetScaffoldDirectory(rootDirectory, s_pythonLanguage);

            Assert.Equal(rootDirectory.FullName, scaffoldDirectory.FullName);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void SerializePackageJson_PreservesTrailingNewLine_WhenOriginalHadOne()
    {
        var packageJson = JsonNode.Parse("""{ "scripts": { "aspire:start": "npm --prefix aspire-apphost run aspire:start" } }""")!.AsObject();

        var serialized = ScaffoldingService.SerializePackageJson(packageJson, "{\n}\n");

        Assert.EndsWith("\n", serialized);
    }

    [Fact]
    public void SerializePackageJson_PreservesTrailingNewLineStyle_WhenOriginalHadWindowsNewLine()
    {
        var packageJson = JsonNode.Parse("""{ "scripts": { "aspire:start": "npm --prefix aspire-apphost run aspire:start" } }""")!.AsObject();

        var serialized = ScaffoldingService.SerializePackageJson(packageJson, "{\r\n}\r\n");

        Assert.EndsWith("\r\n", serialized);
    }

    [Fact]
    public void SerializePackageJson_DoesNotAddTrailingNewLine_WhenOriginalDidNotHaveOne()
    {
        var packageJson = JsonNode.Parse("""{ "scripts": { "aspire:start": "npm --prefix aspire-apphost run aspire:start" } }""")!.AsObject();

        var serialized = ScaffoldingService.SerializePackageJson(packageJson, "{}");

        Assert.False(serialized.EndsWith(Environment.NewLine, StringComparison.Ordinal));
    }

    [Fact]
    public void AddRootTypeScriptAppHostDelegateScripts_AddsMissingScriptsWithSelectedToolchain()
    {
        var scripts = JsonNode.Parse("""{ "test": "vitest" }""")!.AsObject();

        var preservedScriptNames = ScaffoldingService.AddRootTypeScriptAppHostDelegateScripts(
            scripts,
            TypeScriptAppHostToolchain.Pnpm,
            "apps/web/aspire-apphost");

        Assert.Empty(preservedScriptNames);
        Assert.Equal("vitest", scripts["test"]?.GetValue<string>());
        Assert.Equal("pnpm --dir apps/web/aspire-apphost run aspire:start", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("pnpm --dir apps/web/aspire-apphost run aspire:build", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("pnpm --dir apps/web/aspire-apphost run aspire:dev", scripts["aspire:dev"]?.GetValue<string>());
    }

    [Fact]
    public void AddRootTypeScriptAppHostDelegateScripts_UsesDenoTasks()
    {
        var scripts = new JsonObject();

        var preservedScriptNames = ScaffoldingService.AddRootTypeScriptAppHostDelegateScripts(
            scripts,
            TypeScriptAppHostToolchain.Deno,
            "apps/web/aspire-apphost");

        Assert.Empty(preservedScriptNames);
        Assert.Equal("deno task --cwd apps/web/aspire-apphost aspire:start", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("deno task --cwd apps/web/aspire-apphost aspire:build", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("deno task --cwd apps/web/aspire-apphost aspire:dev", scripts["aspire:dev"]?.GetValue<string>());
    }

    [Fact]
    public void AddRootTypeScriptAppHostDelegateScripts_UsesAppHostToolchain()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var appHostDirectory = Directory.CreateDirectory(Path.Combine(rootDirectory.FullName, ScaffoldingService.BrownfieldTypeScriptAppHostDirectoryName));
            File.WriteAllText(Path.Combine(rootDirectory.FullName, "package.json"), "{ \"packageManager\": \"npm@10.0.0\" }");
            File.WriteAllText(Path.Combine(appHostDirectory.FullName, "package.json"), "{ \"packageManager\": \"pnpm@10.0.0\" }");
            var scripts = new JsonObject();

            var preservedScriptNames = ScaffoldingService.AddRootTypeScriptAppHostDelegateScripts(
                scripts,
                appHostDirectory,
                ScaffoldingService.BrownfieldTypeScriptAppHostDirectoryName,
                new TestEnvironment(),
                logger: null);

            Assert.Empty(preservedScriptNames);
            Assert.Equal("pnpm --dir aspire-apphost run aspire:start", scripts["aspire:start"]?.GetValue<string>());
            Assert.Equal("pnpm --dir aspire-apphost run aspire:build", scripts["aspire:build"]?.GetValue<string>());
            Assert.Equal("pnpm --dir aspire-apphost run aspire:dev", scripts["aspire:dev"]?.GetValue<string>());
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AddRootTypeScriptAppHostDelegateScripts_PreservesExistingAspireScripts()
    {
        var scripts = JsonNode.Parse("""
            {
              "aspire:start": "custom-start",
              "aspire:build": "npm --prefix aspire-apphost run aspire:build"
            }
            """)!.AsObject();

        var preservedScriptNames = ScaffoldingService.AddRootTypeScriptAppHostDelegateScripts(
            scripts,
            TypeScriptAppHostToolchain.Npm,
            "aspire-apphost");

        Assert.Equal(["aspire:start"], preservedScriptNames);
        Assert.Equal("custom-start", scripts["aspire:start"]?.GetValue<string>());
        Assert.Equal("npm --prefix aspire-apphost run aspire:build", scripts["aspire:build"]?.GetValue<string>());
        Assert.Equal("npm --prefix aspire-apphost run aspire:dev", scripts["aspire:dev"]?.GetValue<string>());
    }

    [Fact]
    public void GetScaffoldedAppHostRelativePath_UsesActualScaffoldedFile_WhenDefaultFileNameDiffers()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var relativePath = ScaffoldingService.GetScaffoldedAppHostRelativePath(
                rootDirectory,
                rootDirectory,
                s_typeScriptLanguage,
                ["apphost.ts"]);

            Assert.Equal("apphost.ts", relativePath);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetScaffoldedAppHostRelativePath_UsesNestedActualScaffoldedFile_ForBrownfieldTypeScript()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var scaffoldDirectory = Directory.CreateDirectory(Path.Combine(rootDirectory.FullName, ScaffoldingService.BrownfieldTypeScriptAppHostDirectoryName));

            var relativePath = ScaffoldingService.GetScaffoldedAppHostRelativePath(
                rootDirectory,
                scaffoldDirectory,
                s_typeScriptLanguage,
                ["apphost.ts"]);

            Assert.Equal("aspire-apphost/apphost.ts", relativePath);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetConflictingScaffoldFiles_IgnoresMergeableFilesButReturnsOtherExistingFiles()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            File.WriteAllText(Path.Combine(rootDirectory.FullName, ".gitignore"), "node_modules/\n");
            File.WriteAllText(Path.Combine(rootDirectory.FullName, "package.json"), "{}");
            File.WriteAllText(Path.Combine(rootDirectory.FullName, "apphost.mts"), string.Empty);

            var conflicts = ScaffoldingService.GetConflictingScaffoldFiles(
                rootDirectory.FullName,
                [".gitignore", "package.json", "apphost.mts"]);

            Assert.Equal(["apphost.mts"], conflicts);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScaffoldAsync_RejectsGitIgnoreSymlinkWithoutChangingConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var targetDirectory = workspace.WorkspaceRoot.CreateSubdirectory("config");
        var targetPath = Path.Combine(targetDirectory.FullName, "shared.gitignore");
        await File.WriteAllTextAsync(targetPath, "custom/\n");
        var gitIgnorePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore");
        try
        {
            File.CreateSymbolicLink(gitIgnorePath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
        }

        var originalConfig = new AspireConfigFile
        {
            Channel = "stable",
            SdkVersion = "13.3.0"
        };
        originalConfig.Save(workspace.WorkspaceRoot.FullName);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        var originalConfigContent = await File.ReadAllTextAsync(configPath);

        var scaffoldingService = CreateScaffoldingService(
            workspace,
            new Dictionary<string, string> { [".gitignore"] = ".aspire/\n" });
        var context = new ScaffoldContext(
            Language: s_typeScriptLanguage,
            TargetDirectory: workspace.WorkspaceRoot,
            ProjectName: "test",
            SdkVersion: "13.4.0",
            Channel: "daily");

        var result = await scaffoldingService.ScaffoldAsync(context, TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(originalConfigContent, await File.ReadAllTextAsync(configPath));
        Assert.NotNull(new FileInfo(gitIgnorePath).LinkTarget);
        Assert.Equal("custom/\n", await File.ReadAllTextAsync(targetPath));
    }

    [Fact]
    public async Task ScaffoldAsync_ReplacesExistingGitIgnoreAtomically()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows does not permit replacing the held-open destination used to observe file identity.");
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var gitIgnorePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".gitignore");
        const string existingContent = "custom/\n";
        await File.WriteAllTextAsync(gitIgnorePath, existingContent);
        // On Unix, an atomic rename leaves an existing handle attached to the old file. An in-place
        // write changes the content seen through this handle, so this verifies the scaffolding call site.
        await using var originalFile = new FileStream(
            gitIgnorePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.Asynchronous);
        var scaffoldingService = CreateScaffoldingService(
            workspace,
            new Dictionary<string, string> { [".gitignore"] = ".aspire/\n" });
        var context = new ScaffoldContext(
            Language: s_typeScriptLanguage,
            TargetDirectory: workspace.WorkspaceRoot,
            ProjectName: "test",
            SdkVersion: "13.4.0",
            Channel: "daily");

        var result = await scaffoldingService.ScaffoldAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal("custom/\n.aspire/\n", await File.ReadAllTextAsync(gitIgnorePath));
        originalFile.Position = 0;
        using var reader = new StreamReader(originalFile, leaveOpen: true);
        Assert.Equal(existingContent, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(workspace.WorkspaceRoot.FullName, ".gitignore.tmp-*"));
    }

    [Fact]
    public void GitIgnoreMerger_AppendsMissingEntriesWithoutOverwritingExistingContent()
    {
        var existingContent = "node_modules/\ncustom/\n";
        var scaffoldContent = "node_modules/\ndist/\n.aspire/\n";

        var mergedContent = GitIgnoreMerger.Merge(existingContent, scaffoldContent);
        var lines = mergedContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(
            ["node_modules/", "custom/", "dist/", ".aspire/"],
            lines);
    }

    [Fact]
    public void GetConflictingScaffoldFiles_TreatsVsCodeSettingsAsMergeable()
    {
        // Almost every existing repository opened in VS Code already has a .vscode/settings.json,
        // and the Java scaffold writes one. Reporting it as a conflict aborts init entirely.
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(rootDirectory.FullName, ".vscode"));
            File.WriteAllText(Path.Combine(rootDirectory.FullName, ".vscode", "settings.json"), "{}");

            var conflicts = ScaffoldingService.GetConflictingScaffoldFiles(
                rootDirectory.FullName,
                [".vscode/settings.json"]);

            Assert.Empty(conflicts);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void MergeVsCodeSettingsContent_AddsMissingSettingsAndKeepsTheDeveloperValues()
    {
        var existingContent = """
            {
              "editor.formatOnSave": true,
              "java.compile.nullAnalysis.mode": "automatic"
            }
            """;
        var scaffoldContent = """
            {
              "java.project.sourcePaths": [".", ".aspire/modules"],
              "java.compile.nullAnalysis.mode": "disabled"
            }
            """;

        var merged = JsonNode.Parse(
            ScaffoldingService.MergeVsCodeSettingsContent(existingContent, scaffoldContent))!.AsObject();

        Assert.True(merged["editor.formatOnSave"]!.GetValue<bool>());
        // A setting the developer chose is theirs, so the scaffold does not overwrite it.
        Assert.Equal("automatic", merged["java.compile.nullAnalysis.mode"]!.GetValue<string>());
        Assert.Equal([".", ".aspire/modules"], merged["java.project.sourcePaths"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void MergeVsCodeSettingsContent_UnionsSourcePathsWithoutDroppingExistingEntries()
    {
        var existingContent = """
            {
              "java.project.sourcePaths": ["src/main/java", "."]
            }
            """;
        var scaffoldContent = """
            {
              "java.project.sourcePaths": [".", ".aspire/modules"]
            }
            """;

        var merged = JsonNode.Parse(
            ScaffoldingService.MergeVsCodeSettingsContent(existingContent, scaffoldContent))!.AsObject();

        Assert.Equal(
            ["src/main/java", ".", ".aspire/modules"],
            merged["java.project.sourcePaths"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void MergeVsCodeSettingsContent_ReadsSettingsThatUseCommentsAndTrailingCommas()
    {
        // VS Code settings are JSONC, and its own UI writes the "// Place your settings" header.
        var existingContent = """
            {
              // Place your settings in this file.
              "editor.tabSize": 4,
            }
            """;
        var scaffoldContent = """
            {
              "java.project.sourcePaths": ["."]
            }
            """;

        var merged = JsonNode.Parse(
            ScaffoldingService.MergeVsCodeSettingsContent(existingContent, scaffoldContent))!.AsObject();

        Assert.Equal(4, merged["editor.tabSize"]!.GetValue<int>());
        Assert.Equal(["."], merged["java.project.sourcePaths"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void MergeVsCodeSettingsContent_PreservesSettingsThatCannotBeParsed()
    {
        // A settings.json broken mid-edit, or one using a JSONC construct the parser does not accept.
        // Overwriting it discards editor configuration the developer may have accumulated for years,
        // and `aspire init` gives no warning that it happened.
        var existingContent = """
            {
              "editor.formatOnSave": true,
              "java.project.sourcePaths": ["src/main/java"
            }
            """;
        var scaffoldContent = """
            {
              "java.project.sourcePaths": [".", ".aspire/modules"]
            }
            """;

        var merged = ScaffoldingService.MergeVsCodeSettingsContent(existingContent, scaffoldContent);

        Assert.Equal(existingContent, merged);
    }

    [Fact]
    public void MergeVsCodeSettingsContent_LeavesTheFileAloneWhenEverySettingIsAlreadyPresent()
    {
        // Nothing to add means nothing is rewritten, so comments and formatting survive re-running init.
        var existingContent = """
            {
              // keep me
              "java.project.sourcePaths": [".", ".aspire/modules"]
            }
            """;
        var scaffoldContent = """
            {
              "java.project.sourcePaths": [".", ".aspire/modules"]
            }
            """;

        Assert.Equal(existingContent, ScaffoldingService.MergeVsCodeSettingsContent(existingContent, scaffoldContent));
    }

    [Fact]
    public void MergeVsCodeSettingsContent_KeepsTheExistingFileWhenItIsNotUsableJson()
    {
        // Preferring the scaffold here would silently discard the developer's whole settings file.
        var merged = ScaffoldingService.MergeVsCodeSettingsContent("not json at all", """{"a": 1}""");

        Assert.Equal("not json at all", merged);
    }

    [Theory]
    [InlineData("node_modules/\n", "/node_modules/\n")]
    [InlineData(".aspire\n", "/.aspire/\n")]
    [InlineData("/.aspire\n", "/.aspire/\n")]
    [InlineData("src/generated/\n", "/src/generated/\n")]
    [InlineData("/src/generated/\n", "src/generated/\n")]
    public void GitIgnoreMerger_DoesNotAddRootedEntryWhenExistingEntryCoversIt(
        string existingContent,
        string scaffoldContent)
    {
        var mergedContent = GitIgnoreMerger.Merge(existingContent, scaffoldContent);

        Assert.Equal(existingContent, mergedContent);
    }

    [Fact]
    public void GitIgnoreMerger_AddsUnrootedEntryWhenOnlyRootedEntryExists()
    {
        const string existingContent = "/node_modules/\n";

        var mergedContent = GitIgnoreMerger.Merge(existingContent, "node_modules/\n");

        Assert.Equal("/node_modules/\nnode_modules/\n", mergedContent);
    }

    [Fact]
    public void GitIgnoreMerger_AddsSlashlessPatternWhenDirectoryOnlyPatternDoesNotCoverIt()
    {
        const string existingContent = ".aspire/\n";

        var mergedContent = GitIgnoreMerger.Merge(existingContent, ".aspire\n");

        Assert.Equal(".aspire/\n.aspire\n", mergedContent);
    }

    [Theory]
    [InlineData("!.aspire/\n")]
    [InlineData("!/.aspire/\n")]
    [InlineData(".*\n!.aspire/\n")]
    [InlineData("!.aspire\n")]
    public void GitIgnoreMerger_HonorsExplicitNegation(string existingContent)
    {
        var mergedContent = GitIgnoreMerger.Merge(existingContent, ".aspire/\n");

        Assert.Equal(existingContent, mergedContent);
    }

    [Theory]
    [InlineData(".aspire/*\n!.aspire/settings.json\n", ".aspire/\n")]
    [InlineData("/.aspire/*\n!/.aspire/settings.json\n", "/.aspire/\n")]
    [InlineData("src/.aspire/*\n!src/.aspire/settings.json\n", ".aspire/\n")]
    public void GitIgnoreMerger_HonorsDescendantNegation(
        string existingContent,
        string scaffoldContent)
    {
        var mergedContent = GitIgnoreMerger.Merge(existingContent, scaffoldContent);

        Assert.Equal(existingContent, mergedContent);
    }

    [Fact]
    public void GitIgnoreMerger_AppendsDirectoryWhenNegationIsUnrelated()
    {
        const string existingContent = ".config/*\n!.config/settings.json\n";

        var mergedContent = GitIgnoreMerger.Merge(existingContent, ".aspire/\n");

        Assert.Equal(".config/*\n!.config/settings.json\n.aspire/\n", mergedContent);
    }

    [Fact]
    public void GitIgnoreMerger_TreatsEscapedNegationAsLiteral()
    {
        const string existingContent = "\\!.aspire/\n";

        var mergedContent = GitIgnoreMerger.Merge(existingContent, ".aspire/\n");

        Assert.Equal("\\!.aspire/\n.aspire/\n", mergedContent);
    }

    [Fact]
    public void GitIgnoreMerger_RepeatedMergeDoesNotDuplicateNegatedEntry()
    {
        const string existingContent = "node_modules/\n";
        const string scaffoldContent = ".vscode/*\n!.vscode/extensions.json\n";

        var firstMerge = GitIgnoreMerger.Merge(existingContent, scaffoldContent);
        var secondMerge = GitIgnoreMerger.Merge(firstMerge, scaffoldContent);

        Assert.Equal("node_modules/\n.vscode/*\n!.vscode/extensions.json\n", firstMerge);
        Assert.Equal(firstMerge, secondMerge);
    }

    private static ScaffoldingService CreateScaffoldingService(
        TemporaryWorkspace workspace,
        IReadOnlyDictionary<string, string> scaffoldFiles)
    {
        return new ScaffoldingService(
            appHostServerProjectFactory: new TestAppHostServerProjectFactory
            {
                CreateAsyncCallback = (path, _) =>
                    Task.FromResult<IAppHostServerProject>(new FakeSucceedingAppHostServerProject(path))
            },
            serverSessionFactory: FakeAppHostServerSessionFactory.CreateForScaffolding(scaffoldFiles),
            languageDiscovery: new TestLanguageDiscovery(s_typeScriptLanguage),
            interactionService: new TestInteractionService(),
            environment: new TestEnvironment(),
            logger: NullLogger<ScaffoldingService>.Instance,
            executionContext: workspace.CreateExecutionContext(),
            profilingTelemetry: new ProfilingTelemetry(new ConfigurationBuilder().Build()));
    }
}
