// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public class MigrateCommandTests(ITestOutputHelper outputHelper)
{
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

    private static ServiceCollection CreateServices(TemporaryWorkspace workspace, ITestOutputHelper outputHelper)
    {
        return (ServiceCollection)CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            // The real factory would try to spin up the Node toolchain when regenerating the SDK.
            // The test factory returns null for apphost.mts so the migrate command skips regeneration.
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory();
        });
    }

    [Fact]
    public async Task MigrateCommand_WithLegacyAppHost_MigratesToMts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot;
        await WriteLegacyLayoutAsync(root);

        var services = CreateServices(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("migrate --yes");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(Aspire.Cli.CliExitCodes.Success, exitCode);

        Assert.False(File.Exists(Path.Combine(root.FullName, "apphost.ts")));
        Assert.False(Directory.Exists(Path.Combine(root.FullName, ".modules")));

        var modernContent = await File.ReadAllTextAsync(Path.Combine(root.FullName, "apphost.mts"));
        Assert.Equal(ExpectedModernAppHostContent, modernContent);

        await AssertMigratedMetadataAsync(
            root,
            new[] { "apphost.mts", ".aspire/modules/aspire.mts", ".aspire/modules/base.mts", ".aspire/modules/transport.mts", "src/**/*.ts", "lib/foo.ts" });
    }

    [Fact]
    public async Task MigrateCommand_WithJsoncTsConfig_RewritesIncludes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
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

        var services = CreateServices(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("migrate --yes");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(Aspire.Cli.CliExitCodes.Success, exitCode);

        await AssertMigratedMetadataAsync(
            root,
            new[] { "apphost.mts", ".aspire/modules/aspire.mts", ".aspire/modules/aspire.d.ts", "src/**/*.ts" });
    }

    [Fact]
    public async Task MigrateCommand_WithNoLegacyAppHost_IsNoOpSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"),
            "import { createBuilder } from './.aspire/modules/aspire.mjs';");

        var services = CreateServices(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("migrate --yes");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(Aspire.Cli.CliExitCodes.Success, exitCode);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts")));
    }

    [Fact]
    public async Task MigrateCommand_RunTwice_SecondRunIsNoOp()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot;
        await WriteLegacyLayoutAsync(root);

        var services = CreateServices(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var firstExitCode = await command.Parse("migrate --yes").InvokeAsync().DefaultTimeout();
        Assert.Equal(Aspire.Cli.CliExitCodes.Success, firstExitCode);

        var migratedContent = await File.ReadAllTextAsync(Path.Combine(root.FullName, "apphost.mts"));

        var secondExitCode = await command.Parse("migrate --yes").InvokeAsync().DefaultTimeout();
        Assert.Equal(Aspire.Cli.CliExitCodes.Success, secondExitCode);

        Assert.Equal(migratedContent, await File.ReadAllTextAsync(Path.Combine(root.FullName, "apphost.mts")));
    }

    [Fact]
    public async Task MigrateCommand_WhenDeclined_DoesNotApplyMigration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot;
        await WriteLegacyLayoutAsync(root);

        var services = (ServiceCollection)CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory();
            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                ConfirmCallback = (_, _) => false
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("migrate");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(Aspire.Cli.CliExitCodes.Success, exitCode);

        Assert.True(File.Exists(Path.Combine(root.FullName, "apphost.ts")));
        Assert.False(File.Exists(Path.Combine(root.FullName, "apphost.mts")));
        Assert.True(Directory.Exists(Path.Combine(root.FullName, ".modules")));

        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "aspire.config.json")))!;
        Assert.Equal("apphost.ts", config["appHost"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task MigrateCommand_WhenConfirmed_AppliesMigration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot;
        await WriteLegacyLayoutAsync(root);

        var services = (ServiceCollection)CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory();
            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                ConfirmCallback = (_, _) => true
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("migrate");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(Aspire.Cli.CliExitCodes.Success, exitCode);

        Assert.False(File.Exists(Path.Combine(root.FullName, "apphost.ts")));
        var modernContent = await File.ReadAllTextAsync(Path.Combine(root.FullName, "apphost.mts"));
        Assert.Equal(ExpectedModernAppHostContent, modernContent);
    }

    [Theory]
    [InlineData("migrate --non-interactive")]
    [InlineData("--non-interactive migrate")]
    public async Task MigrateCommand_FailsFastWhenNonInteractiveWithoutYes(string commandLine)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot;
        await WriteLegacyLayoutAsync(root);

        var services = CreateServices(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(commandLine);
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(Aspire.Cli.CliExitCodes.InvalidCommand, exitCode);
        var error = Assert.Single(result.Errors);
        Assert.Equal(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, SharedCommandStrings.NonInteractiveRequiresYesFormat, "migrate"),
            error.Message);

        // The legacy layout must be untouched when the command fails fast.
        Assert.True(File.Exists(Path.Combine(root.FullName, "apphost.ts")));
        Assert.False(File.Exists(Path.Combine(root.FullName, "apphost.mts")));
    }
}
