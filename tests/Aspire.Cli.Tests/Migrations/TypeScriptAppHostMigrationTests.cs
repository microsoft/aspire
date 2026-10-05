// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Migrations;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Migrations;

public class TypeScriptAppHostMigrationTests(ITestOutputHelper outputHelper)
{
    private static readonly LanguageInfo s_typeScriptLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript (Node.js)",
        PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
        DetectionPatterns: ["apphost.mts", "apphost.ts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.mts");

    private static TypeScriptAppHostMigration CreateMigration(TemporaryWorkspace workspace)
    {
        return new TypeScriptAppHostMigration(
            new NoProjectFileProjectLocator(),
            new TestLanguageDiscovery(s_typeScriptLanguage),
            new TestAppHostProjectFactory(),
            new TestInteractionService(),
            workspace.CreateExecutionContext(),
            NullLogger<TypeScriptAppHostMigration>.Instance);
    }

    [Fact]
    public void Order_Is100()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        Assert.Equal(100, CreateMigration(workspace).Order);
    }

    [Fact]
    public void Id_IsTypeScriptAppHostMts()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        Assert.Equal("typescript-apphost-mts", CreateMigration(workspace).Id);
    }

    [Fact]
    public async Task DetectAsync_WithLegacyAppHost_ReturnsDescriptor()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "import { createBuilder } from './.modules/aspire.js';");

        var descriptor = await CreateMigration(workspace).DetectAsync(MigrationContext.CurrentDirectory, CancellationToken.None);

        Assert.NotNull(descriptor);
        Assert.Contains("apphost.ts", descriptor.Detail);
        Assert.NotNull(descriptor.Metadata);
        Assert.Equal(KnownLanguageId.TypeScript, descriptor.Metadata["language"]!.GetValue<string>());
        Assert.Equal(appHostPath, descriptor.Metadata["appHostPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task DetectAsync_WithModernAppHost_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"),
            "import { createBuilder } from './.aspire/modules/aspire.mjs';");

        var descriptor = await CreateMigration(workspace).DetectAsync(MigrationContext.CurrentDirectory, CancellationToken.None);

        Assert.Null(descriptor);
    }

    [Fact]
    public async Task DetectAsync_WithBothAppHosts_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), "// legacy");
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"), "// modern");

        var descriptor = await CreateMigration(workspace).DetectAsync(MigrationContext.CurrentDirectory, CancellationToken.None);

        Assert.Null(descriptor);
    }

    [Fact]
    public async Task DetectAsync_WithNonTypeScriptAppHost_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"), "// csharp");

        var descriptor = await CreateMigration(workspace).DetectAsync(MigrationContext.CurrentDirectory, CancellationToken.None);

        Assert.Null(descriptor);
    }

    [Fact]
    public async Task DetectAsync_WithSelectedLegacyAppHostOutsideWorkingDirectory_ReturnsDescriptor()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "external-apphost"));
        await WriteLegacyLayoutAsync(appHostDirectory);
        var appHostPath = Path.Combine(appHostDirectory.FullName, "apphost.ts");

        var descriptor = await CreateMigration(workspace).DetectAsync(new MigrationContext(new FileInfo(appHostPath)), CancellationToken.None);

        Assert.NotNull(descriptor);
        Assert.Equal(appHostPath, descriptor.Metadata!["appHostPath"]!.GetValue<string>());
    }

    private const string LegacyAppHostContent =
        """
        import { createBuilder } from './.modules/aspire.js';

        const builder = createBuilder();
        await builder.build();
        """;

    private const string ExpectedModernAppHostContent =
        """
        import { createBuilder } from './.aspire/modules/aspire.mjs';

        const builder = createBuilder();
        await builder.build();
        """;

    private const string LegacyEslintConfigContent =
        """
        export default [
          {
            files: ['apphost.ts']
          }
        ];
        """;

    private const string ExpectedModernEslintConfigContent =
        """
        export default [
          {
            files: ['apphost.mts']
          }
        ];
        """;

    private static async Task WriteLegacyLayoutAsync(DirectoryInfo root, string? tsConfigContent = null)
    {
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "apphost.ts"), LegacyAppHostContent);
        await File.WriteAllTextAsync(
            Path.Combine(root.FullName, "aspire.config.json"),
            """
            {
              "appHost": {
                "path": "apphost.ts"
              }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root.FullName, "tsconfig.apphost.json"),
            tsConfigContent ??
            """
            {
              "include": [ "apphost.ts", ".modules/aspire.ts", ".modules/base.ts", ".modules/transport.ts", "src/**/*.ts", "lib/foo.ts" ]
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root.FullName, "package.json"),
            """
            {
              "type": "module",
              "scripts": {
                "aspire:build": "tsc -p tsconfig.apphost.json",
                "aspire:lint": "eslint apphost.ts"
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "eslint.config.mjs"), LegacyEslintConfigContent);

        var modulesDir = Directory.CreateDirectory(Path.Combine(root.FullName, ".modules"));
        await File.WriteAllTextAsync(Path.Combine(modulesDir.FullName, "aspire.ts"), "// generated");
    }

    private static async Task AssertMigratedMetadataAsync(DirectoryInfo root, string[] expectedIncludes)
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "aspire.config.json")))!;
        Assert.Equal("apphost.mts", config["appHost"]!["path"]!.GetValue<string>());

        var tsconfig = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "tsconfig.apphost.json")))!;
        var includes = tsconfig["include"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(expectedIncludes, includes);

        var packageJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "package.json")))!;
        var scripts = packageJson["scripts"]!;
        Assert.Equal("tsc -p tsconfig.apphost.json", scripts["aspire:build"]!.GetValue<string>());
        Assert.Equal("eslint apphost.mts", scripts["aspire:lint"]!.GetValue<string>());

        var eslintConfig = await File.ReadAllTextAsync(Path.Combine(root.FullName, "eslint.config.mjs"));
        Assert.Equal(ExpectedModernEslintConfigContent, eslintConfig);
    }

    [Fact]
    public async Task ApplyAsync_WithLegacyAppHost_MigratesToMts()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = workspace.WorkspaceRoot;
        await WriteLegacyLayoutAsync(root);

        await CreateMigration(workspace).ApplyAsync(MigrationContext.CurrentDirectory, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(root.FullName, "apphost.ts")));
        Assert.False(Directory.Exists(Path.Combine(root.FullName, ".modules")));

        var modernContent = await File.ReadAllTextAsync(Path.Combine(root.FullName, "apphost.mts"));
        Assert.Equal(ExpectedModernAppHostContent, modernContent);

        await AssertMigratedMetadataAsync(
            root,
            new[] { "apphost.mts", ".aspire/modules/aspire.mts", ".aspire/modules/base.mts", ".aspire/modules/transport.mts", "src/**/*.ts", "lib/foo.ts" });
    }

    [Fact]
    public async Task ApplyAsync_WithSelectedLegacyAppHostOutsideWorkingDirectory_MigratesSelectedAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "external-apphost"));
        await WriteLegacyLayoutAsync(appHostDirectory);

        await CreateMigration(workspace).ApplyAsync(new MigrationContext(new FileInfo(Path.Combine(appHostDirectory.FullName, "apphost.ts"))), CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(appHostDirectory.FullName, "apphost.ts")));
        Assert.False(Directory.Exists(Path.Combine(appHostDirectory.FullName, ".modules")));
        Assert.Equal(ExpectedModernAppHostContent, await File.ReadAllTextAsync(Path.Combine(appHostDirectory.FullName, "apphost.mts")));

        await AssertMigratedMetadataAsync(
            appHostDirectory,
            new[] { "apphost.mts", ".aspire/modules/aspire.mts", ".aspire/modules/base.mts", ".aspire/modules/transport.mts", "src/**/*.ts", "lib/foo.ts" });
    }

    [Fact]
    public async Task ApplyAsync_WithJsoncTsConfig_RewritesIncludes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = workspace.WorkspaceRoot;
        await WriteLegacyLayoutAsync(
            root,
            """
            {
              "include": [
                "apphost.ts",
                ".modules/aspire.ts",
                ".modules/aspire.d.ts",
                "src/**/*.ts", // user code remains TypeScript
              ],
            }
            """);

        await CreateMigration(workspace).ApplyAsync(MigrationContext.CurrentDirectory, CancellationToken.None);

        await AssertMigratedMetadataAsync(
            root,
            new[] { "apphost.mts", ".aspire/modules/aspire.mts", ".aspire/modules/aspire.d.ts", "src/**/*.ts" });
    }

    [Fact]
    public async Task ApplyAsync_RunTwice_SecondRunIsNoOp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = workspace.WorkspaceRoot;
        await WriteLegacyLayoutAsync(root);

        var migration = CreateMigration(workspace);
        await migration.ApplyAsync(MigrationContext.CurrentDirectory, CancellationToken.None);

        var migratedContent = await File.ReadAllTextAsync(Path.Combine(root.FullName, "apphost.mts"));

        await migration.ApplyAsync(MigrationContext.CurrentDirectory, CancellationToken.None);

        Assert.Equal(migratedContent, await File.ReadAllTextAsync(Path.Combine(root.FullName, "apphost.mts")));
    }
}
