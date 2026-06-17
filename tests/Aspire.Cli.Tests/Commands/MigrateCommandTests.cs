// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
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

    private static async Task WriteLegacyLayoutAsync(DirectoryInfo root)
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
            """
            {
              "include": [ "apphost.ts", ".modules/aspire.ts", ".modules/base.ts", ".modules/transport.ts" ]
            }
            """);

        var modulesDir = Directory.CreateDirectory(Path.Combine(root.FullName, ".modules"));
        await File.WriteAllTextAsync(Path.Combine(modulesDir.FullName, "aspire.ts"), "// generated");
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

        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "aspire.config.json")))!;
        Assert.Equal("apphost.mts", config["appHost"]!["path"]!.GetValue<string>());

        var tsconfig = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "tsconfig.apphost.json")))!;
        var includes = tsconfig["include"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(
            new[] { "apphost.mts", ".aspire/modules/aspire.mts", ".aspire/modules/base.mts", ".aspire/modules/transport.mts" },
            includes);
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
}
