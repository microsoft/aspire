// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

public class LegacyTypeScriptAppHostCheckTests(ITestOutputHelper outputHelper)
{
    private static readonly LanguageInfo s_typeScriptLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript (Node.js)",
        PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
        DetectionPatterns: ["apphost.mts", "apphost.ts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.mts");

    private static LegacyTypeScriptAppHostCheck CreateCheck(TemporaryWorkspace workspace)
    {
        return new LegacyTypeScriptAppHostCheck(
            new NoProjectFileProjectLocator(),
            new TestLanguageDiscovery(s_typeScriptLanguage),
            workspace.CreateExecutionContext(),
            NullLogger<LegacyTypeScriptAppHostCheck>.Instance);
    }

    [Fact]
    public async Task CheckAsync_WithLegacyAppHost_ReturnsWarning()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts");
        await File.WriteAllTextAsync(appHostPath, "import { createBuilder } from './.modules/aspire.js';");

        var results = await CreateCheck(workspace).CheckAsync();

        var result = Assert.Single(results);
        Assert.Equal(EnvironmentCheckCategories.AppHost, result.Category);
        Assert.Equal(LegacyTypeScriptAppHostCheck.CheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Contains("apphost.ts", result.Message);
        Assert.NotNull(result.Fix);
    }

    [Fact]
    public async Task CheckAsync_WithModernAppHost_ReturnsEmpty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"),
            "import { createBuilder } from './.aspire/modules/aspire.mjs';");

        var results = await CreateCheck(workspace).CheckAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_WithBothAppHosts_ReturnsEmpty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), "// legacy");
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"), "// modern");

        var results = await CreateCheck(workspace).CheckAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_WithNonTypeScriptAppHost_ReturnsEmpty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"), "// csharp");

        var results = await CreateCheck(workspace).CheckAsync();

        Assert.Empty(results);
    }

    [Fact]
    public void Order_Is102()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        Assert.Equal(102, CreateCheck(workspace).Order);
    }
}
