// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Scaffolding;

/// <summary>
/// Verifies that <see cref="ScaffoldingService.ScaffoldAsync"/> writes the same
/// <c>.codegen-version</c> marker that <see cref="GuestAppHostProject"/> writes on every
/// subsequent <c>aspire run</c>. Without it, the extension's version-marker-based auto-restore
/// check (see <c>AspirePackageRestoreProvider</c>) sees no marker after a fresh <c>aspire init</c>
/// and treats the project as always-stale, forcing one redundant restore the first time VS Code
/// inspects a newly-scaffolded guest-language AppHost.
/// </summary>
public class CodeGenerationVersionMarkerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ScaffoldAsync_WritesCodeGenerationVersionMarker()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string identityVersion = "13.4.0";
        const string identityCommit = "abc1234";

        var scaffoldingService = new ScaffoldingService(
            appHostServerProjectFactory: new TestAppHostServerProjectFactory
            {
                CreateAsyncCallback = (appPath, _) => Task.FromResult<IAppHostServerProject>(new FakeSucceedingAppHostServerProject(appPath))
            },
            serverSessionFactory: new FakeAppHostServerSessionFactory
            {
                Session = new FakeAppHostServerSession(new SucceedingScaffoldRpcClient())
            },
            languageDiscovery: new TestLanguageDiscovery(s_testLanguage),
            interactionService: new TestInteractionService(),
            environment: new TestEnvironment(),
            logger: NullLogger<ScaffoldingService>.Instance,
            executionContext: workspace.CreateExecutionContext(identityVersion: identityVersion, identityCommit: identityCommit),
            profilingTelemetry: new ProfilingTelemetry(new ConfigurationBuilder().Build()));

        var context = new ScaffoldContext(
            Language: s_testLanguage,
            TargetDirectory: workspace.WorkspaceRoot,
            ProjectName: "test",
            SdkVersion: "13.4.0");

        var result = await scaffoldingService.ScaffoldAsync(context, CancellationToken.None);

        Assert.True(result);

        var markerPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            LanguageInfo.GeneratedFolderName,
            GuestAppHostProject.CodeGenerationVersionFileName);

        Assert.True(File.Exists(markerPath));
        var markerContent = await File.ReadAllTextAsync(markerPath);
        Assert.Equal($"{identityVersion}+{identityCommit}", markerContent);
    }

    // Deliberately not TypeScript: TypeScript scaffolding resolves a real package-manager
    // toolchain (npm/yarn/pnpm/bun/deno) via TypeScriptAppHostToolchainResolver before installing
    // dependencies. Using a non-TypeScript language keeps GuestRuntime's dependency-install step
    // a guaranteed no-op (the fake RPC client's default RuntimeSpec has null Initialize/
    // InstallDependencies), so this test never spawns a real process.
    private static readonly LanguageInfo s_testLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.Python),
        DisplayName: KnownLanguageId.PythonDisplayName,
        PackageName: "aspire-app-host",
        DetectionPatterns: ["apphost.py"],
        CodeGenerator: "python",
        AppHostFileName: "apphost.py");

    private sealed class SucceedingScaffoldRpcClient : FakeAppHostRpcClient
    {
        public override Task<Dictionary<string, string>> ScaffoldAppHostAsync(string languageId, string targetPath, string? projectName, CancellationToken cancellationToken)
            => Task.FromResult(new Dictionary<string, string>());
    }
}
