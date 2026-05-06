// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Scaffolding;

/// <summary>
/// Regression tests for project-channel reseed sites, ensuring that when saving the resolved
/// CLI channel to a project's aspire.config.json during scaffolding or initialization, the value
/// persisted is the one from <see cref="CliExecutionContext.Channel"/> (the resolved, consumer-facing
/// label: <c>pr-&lt;N&gt;</c> for PR builds, identity channel verbatim otherwise).
/// <para>
/// Earlier tests inlined the production expression and round-tripped it through
/// <see cref="AspireConfigFile"/>. Those tests gave false confidence — they could pass against
/// a regression that replaced <c>_executionContext.Channel</c> with a literal string. The tests
/// here exercise the actual production codepath where possible, and otherwise lock the source-level
/// reference shape so the regression cannot land silently.
/// </para>
/// </summary>
public class ChannelReseedTests
{
    // The following reseed call sites must write the resolved channel from CliExecutionContext.Channel:
    // Behavioral coverage exists for ScaffoldingService below; the others are covered by source-level
    // + structural reflection guards because they sit behind heavyweight DI (AppHostServerProjectFactory
    // + RPC + project I/O) that this unit-test layer cannot reasonably stand up.
    //
    //   ScaffoldingService.cs                               — line 75 (early-save)  ← behavioral
    //   ScaffoldingService.cs                               — line 208 (post-prepare)
    //   Templating/CliTemplateFactory.PythonStarterTemplate — line 79
    //   Templating/CliTemplateFactory.GoStarterTemplate     — (parallel pattern)
    //   Projects/GuestAppHostProject.cs                     — lines 349, 1213

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

    // The Python starter template factory and GuestAppHostProject reseed sites are
    // structurally identical to ScaffoldingService — but they sit behind much heavier
    // DI surfaces (template tree extraction, project factory, codegen RPC). Behavior
    // tests would require setting up most of the CLI host. Instead, we lock the
    // source-level reference shape: each site MUST read from the
    // CliExecutionContext field (not a literal). A future change that replaces the
    // dynamic read with a hard-coded string will fail these tests.

    [Fact]
    public void PythonStarterTemplate_ReseedSite_ReadsExecutionContextChannel()
    {
        var source = LoadSourceFile("src/Aspire.Cli/Templating/CliTemplateFactory.PythonStarterTemplate.cs");

        // Production line: var seedChannel = !string.IsNullOrEmpty(inputs.Channel)
        //                      ? inputs.Channel : _executionContext.Channel;
        Assert.Contains("_executionContext.Channel", source);
        Assert.Contains("inputs.Channel", source);
        Assert.Contains("config.Channel = seedChannel", source);
    }

    [Fact]
    public void GoStarterTemplate_ReseedSite_ReadsExecutionContextChannel()
    {
        var source = LoadSourceFile("src/Aspire.Cli/Templating/CliTemplateFactory.GoStarterTemplate.cs");

        Assert.Contains("_executionContext.Channel", source);
    }

    [Fact]
    public void GuestAppHostProject_ReseedSites_ReadExecutionContextChannel()
    {
        var source = LoadSourceFile("src/Aspire.Cli/Projects/GuestAppHostProject.cs");

        // Two reseed call sites in this file (build-result fallback and
        // PrepareAsync channel-record). Both MUST source from _executionContext.Channel.
        var hits = CountOccurrences(source, "_executionContext.Channel");
        Assert.True(hits >= 2, $"Expected ≥2 references to _executionContext.Channel in GuestAppHostProject.cs, found {hits}.");
    }

    [Fact]
    public void ScaffoldingService_ReseedSites_ReadExecutionContextChannel()
    {
        var source = LoadSourceFile("src/Aspire.Cli/Scaffolding/ScaffoldingService.cs");

        // Two reseed call sites: the early save and the post-prepare save.
        var hits = CountOccurrences(source, "_cliExecutionContext.Channel");
        Assert.True(hits >= 2, $"Expected ≥2 references to _cliExecutionContext.Channel in ScaffoldingService.cs, found {hits}.");
    }

    // Structural reflection guards: the constructor-injected dependency exists.
    // Without these, all the source-level guards above could pass while the type
    // had stopped accepting the dependency entirely.

    [Fact]
    public void ScaffoldingService_HoldsCliExecutionContextDependency()
    {
        var field = typeof(ScaffoldingService)
            .GetField("_cliExecutionContext", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal(typeof(CliExecutionContext), field.FieldType);
    }

    [Fact]
    public void GuestAppHostProject_HoldsCliExecutionContextDependency()
    {
        var field = typeof(GuestAppHostProject)
            .GetField("_executionContext", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal(typeof(CliExecutionContext), field.FieldType);
    }

    [Fact]
    public void CliTemplateFactory_HoldsCliExecutionContextDependency()
    {
        var field = typeof(Aspire.Cli.Templating.CliTemplateFactory)
            .GetField("_executionContext", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal(typeof(CliExecutionContext), field.FieldType);
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

    private static string LoadSourceFile(string repoRelativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(GetRepoRoot(), repoRelativePath));
        Assert.True(File.Exists(fullPath), $"Expected source file at {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string GetRepoRoot()
    {
        // Walk up from the test bin output until we find the repo root (global.json sits there).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static int CountOccurrences(string source, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = source.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
