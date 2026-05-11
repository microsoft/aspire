// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Scaffolding;

/// <summary>
/// Behavioral regression tests for channel reseed in <see cref="ScaffoldingService.ScaffoldAsync"/>.
/// Verifies that the channel written to <c>aspire.config.json</c> is sourced from
/// <see cref="CliExecutionContext.Channel"/> — not a literal — for both the implicit (context) and
/// explicit (caller-supplied) cases.
/// <para>
/// <b>Coverage gap:</b> The heavyweight DI reseed sites —
/// <c>CliTemplateFactory.PythonStarterTemplate</c>, <c>CliTemplateFactory.GoStarterTemplate</c>,
/// and <c>GuestAppHostProject</c> — are NOT covered at this unit-test layer because they sit behind
/// template extraction, project factory, and codegen RPC that this layer cannot reasonably stand up.
/// Reseed regressions at those sites must be caught by integration tests or dogfood.
/// </para>
/// </summary>
public class ChannelReseedTests
{
    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    [InlineData("pr-12345")] // option-(a) resolved label — what reseed sites must persist
    public async Task ScaffoldAsync_NoExplicitChannel_PersistsCliExecutionContextChannel(string contextChannel)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var executionContext = CreateExecutionContext(contextChannel);
            var scaffoldingService = CreateScaffoldingService(executionContext);

            var ctx = new ScaffoldContext(
                Language: s_testLanguage,
                TargetDirectory: dir,
                ProjectName: "test",
                SdkVersion: null,
                Channel: null);

            // ScaffoldGuestLanguageAsync writes the early channel save to disk
            // BEFORE the AppHostServerProject is created — so we capture the
            // reseed even though IAppHostServerProjectFactory.CreateAsync throws.
            await Assert.ThrowsAnyAsync<Exception>(
                async () => await scaffoldingService.ScaffoldAsync(ctx, CancellationToken.None));

            var reloaded = AspireConfigFile.Load(dir.FullName);
            Assert.NotNull(reloaded);
            Assert.Equal(contextChannel, reloaded.Channel);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ScaffoldAsync_ExplicitChannel_OverridesCliExecutionContextChannel()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var executionContext = CreateExecutionContext(channel: "daily");
            var scaffoldingService = CreateScaffoldingService(executionContext);

            var ctx = new ScaffoldContext(
                Language: s_testLanguage,
                TargetDirectory: dir,
                ProjectName: "test",
                SdkVersion: null,
                Channel: "explicit-staging");

            await Assert.ThrowsAnyAsync<Exception>(
                async () => await scaffoldingService.ScaffoldAsync(ctx, CancellationToken.None));

            var reloaded = AspireConfigFile.Load(dir.FullName);
            Assert.NotNull(reloaded);
            Assert.Equal("explicit-staging", reloaded.Channel);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static readonly LanguageInfo s_testLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript",
        PackageName: string.Empty,
        DetectionPatterns: ["apphost.ts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.ts");

    private static CliExecutionContext CreateExecutionContext(string channel)
    {
        // For "pr-<N>" we still call through the regular ctor with channel="pr" + prNumber
        // so that CliExecutionContext.Channel resolves option-(a). For non-pr values the
        // channel is passed verbatim.
        if (channel.StartsWith("pr-", StringComparison.Ordinal) &&
            int.TryParse(channel.AsSpan(3), out var prNumber))
        {
            return BuildContext(channel: "pr", prNumber: prNumber);
        }

        return BuildContext(channel: channel, prNumber: null);
    }

    private static CliExecutionContext BuildContext(string channel, int? prNumber)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        return new CliExecutionContext(
            workingDirectory: dir,
            hivesDirectory: dir,
            cacheDirectory: dir,
            sdksDirectory: dir,
            logsDirectory: dir,
            logFilePath: "test.log",
            channel: channel,
            prNumber: prNumber);
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
