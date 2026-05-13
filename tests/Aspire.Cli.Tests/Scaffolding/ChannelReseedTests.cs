// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Scaffolding;

/// <summary>
/// Behavioral regression tests for channel reseed in <see cref="ScaffoldingService.ScaffoldAsync"/>.
/// Verifies that the channel written to <c>aspire.config.json</c> is sourced from
/// <see cref="CliExecutionContext.IdentityChannel"/> when no explicit channel is given, and from the
/// caller-supplied value when one is.
/// <para>
/// <b>Coverage gap:</b> The heavyweight DI reseed sites —
/// <c>CliTemplateFactory.PythonStarterTemplate</c>, <c>CliTemplateFactory.GoStarterTemplate</c>,
/// and <c>GuestAppHostProject</c> — are NOT covered at this unit-test layer because they sit behind
/// template extraction, project factory, and codegen RPC that this layer cannot reasonably stand up.
/// Reseed regressions at those sites must be caught by integration tests or dogfood.
/// </para>
/// </summary>
public class ChannelReseedTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("stable", null, "stable")]
    [InlineData("staging", null, "staging")]
    [InlineData("daily", null, "daily")]
    [InlineData("pr-12345", null, "pr-12345")]                  // option-(a) resolved label — what reseed sites must persist
    [InlineData("daily", "explicit-staging", "explicit-staging")] // explicit Channel overrides context.IdentityChannel
    public async Task ScaffoldAsync_PersistsExpectedChannel(string contextChannel, string? explicitChannel, string expectedPersistedChannel)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = BuildContext(contextChannel);
        var scaffoldingService = CreateScaffoldingService(executionContext);

        var ctx = new ScaffoldContext(
            Language: s_testLanguage,
            TargetDirectory: workspace.WorkspaceRoot,
            ProjectName: "test",
            SdkVersion: null,
            Channel: explicitChannel);

        // ScaffoldGuestLanguageAsync writes the early channel save to disk
        // BEFORE the AppHostServerProject is created — so we capture the
        // reseed even though IAppHostServerProjectFactory.CreateAsync throws.
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await scaffoldingService.ScaffoldAsync(ctx, CancellationToken.None));

        var reloaded = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);
        Assert.NotNull(reloaded);
        Assert.Equal(expectedPersistedChannel, reloaded.Channel);
    }

    private static readonly LanguageInfo s_testLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript",
        PackageName: string.Empty,
        DetectionPatterns: ["apphost.ts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.ts");

    private static CliExecutionContext BuildContext(string channel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        return new CliExecutionContext(
            workingDirectory: dir,
            hivesDirectory: dir,
            cacheDirectory: dir,
            sdksDirectory: dir,
            logsDirectory: dir,
            logFilePath: "test.log",
            identityChannel: channel);
    }

    private static ScaffoldingService CreateScaffoldingService(CliExecutionContext executionContext)
    {
        return new ScaffoldingService(
            appHostServerProjectFactory: new TestAppHostServerProjectFactory(),
            languageDiscovery: new TestLanguageDiscovery(s_testLanguage),
            interactionService: new TestInteractionService(),
            cliExecutionContext: executionContext,
            logger: NullLogger<ScaffoldingService>.Instance);
    }
}

