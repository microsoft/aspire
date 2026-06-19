// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        Assert.Equal(100, CreateMigration(workspace).Order);
    }

    [Fact]
    public void Id_IsTypeScriptAppHostMts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        Assert.Equal("typescript-apphost-mts", CreateMigration(workspace).Id);
    }

    [Fact]
    public async Task DetectAsync_WithLegacyAppHost_ReturnsDescriptor()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "import { createBuilder } from './.modules/aspire.js';");

        var descriptor = await CreateMigration(workspace).DetectAsync(CancellationToken.None);

        Assert.NotNull(descriptor);
        Assert.Contains("apphost.ts", descriptor.Detail);
        Assert.NotNull(descriptor.Metadata);
        Assert.Equal(KnownLanguageId.TypeScript, descriptor.Metadata["language"]!.GetValue<string>());
        Assert.Equal(appHostPath, descriptor.Metadata["appHostPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task DetectAsync_WithModernAppHost_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"),
            "import { createBuilder } from './.aspire/modules/aspire.mjs';");

        var descriptor = await CreateMigration(workspace).DetectAsync(CancellationToken.None);

        Assert.Null(descriptor);
    }

    [Fact]
    public async Task DetectAsync_WithBothAppHosts_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), "// legacy");
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"), "// modern");

        var descriptor = await CreateMigration(workspace).DetectAsync(CancellationToken.None);

        Assert.Null(descriptor);
    }

    [Fact]
    public async Task DetectAsync_WithNonTypeScriptAppHost_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"), "// csharp");

        var descriptor = await CreateMigration(workspace).DetectAsync(CancellationToken.None);

        Assert.Null(descriptor);
    }
}
