// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace Aspire.Cli.Tests.Scaffolding;

/// <summary>
/// Behavioral regression tests for channel reseed in <see cref="ScaffoldingService.ScaffoldAsync"/>.
/// Verifies that the channel written to <c>aspire.config.json</c> mirrors
/// <see cref="ScaffoldContext.Channel"/> verbatim and that <see cref="ScaffoldingService"/>
/// does NOT fall back to <see cref="CliExecutionContext.IdentityChannel"/>.
/// <para>
/// All channel selection happens upstream of <see cref="ScaffoldingService"/>: <c>NewCommand</c>
/// resolves the channel from <c>--channel</c> or an identity-match against a registered
/// Explicit channel and passes it through <see cref="ScaffoldContext.Channel"/>; <c>aspire init</c>
/// passes <see langword="null"/>. Pinning <see cref="CliExecutionContext.IdentityChannel"/> when
/// the caller didn't ask would mismatch <c>aspire.config.json#channel</c> with the running CLI
/// when the identity isn't a registered Explicit channel (e.g. <c>pr-&lt;N&gt;</c> on a machine
/// without the matching hive, or <c>staging</c> without the staging feature flag).
/// <c>PrebuiltAppHostServer</c> aggregates sources from every registered channel when no pin is
/// present, so <c>aspire add</c> / <c>aspire restore</c> still find the right packages.
/// </para>
/// <para>
/// The three CLI starter template factories
/// (<c>CliTemplateFactory.{TypeScript,Python,Go}StarterTemplate</c>) have the same semantic — they
/// only persist <c>aspire.config.json#channel</c> when their <c>TemplateInputs.Channel</c> input
/// is set. Channel selection for those paths is covered by <c>NewCommandChannelResolutionTests</c>;
/// the end-to-end consistency between resolved channel and persisted SDK version is covered by
/// <c>TypeScriptStarterSmokeTests</c> on the daily smoke workflow.
/// </para>
/// </summary>
public class ChannelReseedTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(null, null)]                            // aspire init polyglot — no caller channel, no pin written
    [InlineData("daily", "daily")]                      // NewCommand resolved Explicit identity-match → pin written
    [InlineData("pr-12345", "pr-12345")]                // PR-built CLI with matching hive → pin written
    [InlineData("explicit-staging", "explicit-staging")] // user-supplied --channel → pin written
    public async Task ScaffoldAsync_PersistsContextChannelVerbatim(string? contextChannel, string? expectedPersistedChannel)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var scaffoldingService = CreateScaffoldingService(workspace);

        var ctx = new ScaffoldContext(
            Language: s_testLanguage,
            TargetDirectory: workspace.WorkspaceRoot,
            ProjectName: "test",
            SdkVersion: null,
            Channel: contextChannel);

        // ScaffoldGuestLanguageAsync writes the early channel save to disk
        // BEFORE the AppHostServerProject is created — so we capture the
        // reseed even though IAppHostServerProjectFactory.CreateAsync throws.
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await scaffoldingService.ScaffoldAsync(ctx, CancellationToken.None));

        var reloaded = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);
        // `LoadOrCreate` migrates the seeded legacy `.aspire/settings.json` to `aspire.config.json`
        // (TemporaryWorkspace seeds a `{}` settings file so directory-walking config lookups stop
        // at the test workspace). So the file is always present; the regression we're guarding
        // is the value written to `channel`, not the existence of the file.
        Assert.NotNull(reloaded);
        Assert.Equal(expectedPersistedChannel, reloaded.Channel);
    }

    [Fact]
    public async Task ScaffoldAsync_PassesPackageSourceOverrideToPrepareAsync()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string packageSourceOverride = "/tmp/aspire-pr-hive/packages";
        var language = s_testLanguage with { PackageName = "Aspire.Hosting.CodeGeneration.TypeScript" };
        var appHostServerProject = new CapturingAppHostServerProject(workspace.WorkspaceRoot.FullName);

        var scaffoldingService = new ScaffoldingService(
            appHostServerProjectFactory: new TestAppHostServerProjectFactory
            {
                CreateAsyncCallback = (_, _) => Task.FromResult<IAppHostServerProject>(appHostServerProject)
            },
            appHostServerSessionFactory: new TestAppHostServerSessionFactory(),
            languageDiscovery: new TestLanguageDiscovery(language),
            interactionService: new TestInteractionService(),
            logger: NullLogger<ScaffoldingService>.Instance,
            executionContext: workspace.CreateExecutionContext(),
            profilingTelemetry: new ProfilingTelemetry(new ConfigurationBuilder().Build()));

        var context = new ScaffoldContext(
            Language: language,
            TargetDirectory: workspace.WorkspaceRoot,
            ProjectName: "test",
            SdkVersion: "13.4.0-pr.17141.gf142085f",
            PackageSourceOverride: packageSourceOverride);

        var result = await scaffoldingService.ScaffoldAsync(context, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(packageSourceOverride, appHostServerProject.PackageSourceOverride);
    }

    private static readonly LanguageInfo s_testLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript",
        PackageName: string.Empty,
        DetectionPatterns: ["apphost.ts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.ts");

    private static ScaffoldingService CreateScaffoldingService(TemporaryWorkspace workspace)
    {
        return new ScaffoldingService(
            appHostServerProjectFactory: new TestAppHostServerProjectFactory(),
            appHostServerSessionFactory: new TestAppHostServerSessionFactory(),
            languageDiscovery: new TestLanguageDiscovery(s_testLanguage),
            interactionService: new TestInteractionService(),
            logger: NullLogger<ScaffoldingService>.Instance,
            executionContext: workspace.CreateExecutionContext(),
            profilingTelemetry: new ProfilingTelemetry(new ConfigurationBuilder().Build()));
    }

    private sealed class CapturingAppHostServerProject(string appDirectoryPath) : IAppHostServerProject
    {
        public string AppDirectoryPath { get; } = appDirectoryPath;

        public string? PackageSourceOverride { get; private set; }

        public string GetInstanceIdentifier() => AppDirectoryPath;

        public Task<AppHostServerPrepareResult> PrepareAsync(
            string sdkVersion,
            IEnumerable<IntegrationReference> integrations,
            string? requestedChannel = null,
            string? packageSourceOverride = null,
            CancellationToken cancellationToken = default)
        {
            PackageSourceOverride = packageSourceOverride;
            return Task.FromResult(new AppHostServerPrepareResult(Success: false, Output: null));
        }

        public (string SocketPath, Process Process, OutputCollector OutputCollector) Run(
            int hostPid,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            string[]? additionalArgs = null,
            bool debug = false) =>
            throw new NotSupportedException("Run should not be invoked when PrepareAsync fails.");
    }
}
