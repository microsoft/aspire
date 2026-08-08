// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Commands.Sdk;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Semver;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Commands.Sdk;

/// <summary>
/// Covers <c>aspire sdk export</c>. The command exists to feed documentation pipelines, so the
/// discipline it needs is unusual for a CLI command: stdout has to be exactly one machine-readable
/// document with nothing else mixed in, and the package version has to be exact so published
/// documentation can be keyed on it.
/// </summary>
public class SdkExportCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task SdkExportWithHelpReturnsZero()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("sdk export --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task SdkExportForExactPackageWritesCanonicalDocumentToStdout()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(interactionService, out var workspace, out var rpcClient);
        using var _ = workspace;

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package Aspire.Hosting.Redis@13.5.0");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(("typescript", "Aspire.Hosting.Redis", "13.5.0"), rpcClient.LastExportRequest);

        var stdout = Assert.Single(interactionService.DisplayedRawText, entry => entry.ConsoleOverride == ConsoleOutput.Standard);
        using var document = JsonDocument.Parse(stdout.Text);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Aspire.Hosting.Redis", document.RootElement.GetProperty("package").GetProperty("name").GetString());
    }

    [Fact]
    public async Task SdkExportDefaultsToCoreHostingAtTheRunningSdkVersion()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(interactionService, out var workspace, out var rpcClient);
        using var _ = workspace;

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript");

        Assert.Equal(CliExitCodes.Success, exitCode);

        // Defaulting to the CLI's own SDK version is the entire point of the command: documentation
        // must describe the SDK this CLI would actually generate against, not a floating latest.
        var expectedVersion = provider.GetRequiredService<Aspire.Cli.CliExecutionContext>().IdentityVersion;
        Assert.Equal(("typescript", "Aspire.Hosting", expectedVersion), rpcClient.LastExportRequest);
    }

    [Fact]
    public async Task SdkExportSendsProgressToStderrOnly()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(interactionService, out var workspace, out _);
        using var _2 = workspace;

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package Aspire.Hosting.Redis@13.5.0 --output " + Path.Combine(workspace.WorkspaceRoot.FullName, "api.json"));

        Assert.Equal(CliExitCodes.Success, exitCode);

        // A null per-call override means the message follows the service's Console, so asserting on
        // the override alone passes vacuously. Resolve the effective destination instead.
        Assert.Equal(ConsoleOutput.Error, interactionService.Console);
        Assert.DoesNotContain(
            interactionService.DisplayedMessages,
            message => (message.ConsoleOverride ?? interactionService.Console) == ConsoleOutput.Standard);

        // DisplaySuccess cannot be overridden per call, so the --output confirmation would land on
        // stdout and corrupt a piped document if the service were not routed to stderr.
        Assert.NotEmpty(interactionService.DisplayedSuccess);
    }

    [Fact]
    public async Task SdkExportPassesPackageSourceThroughToPrepare()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new CapturingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        using var provider = CreateProvider(interactionService, workspace, new StubExportRpcClient(), appHostServerProject);

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package Aspire.Hosting.Redis@13.5.0 --source /tmp/aspire-hive");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("/tmp/aspire-hive", appHostServerProject.PackageSourceOverride);
    }

    /// <summary>
    /// The code generator ships in its own package that the scanner AppHost does not reference by
    /// default. Without adding it the server loads no generators and every export fails with
    /// "No code generator found", which is exactly how this regressed once already.
    /// </summary>
    [Fact]
    public async Task SdkExportAddsTheCodeGenerationPackageForTheRequestedLanguage()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new CapturingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        using var provider = CreateProvider(interactionService, workspace, new StubExportRpcClient(), appHostServerProject);

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package Aspire.Hosting.Redis@13.5.0");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains(
            appHostServerProject.Integrations,
            integration => integration.Name.Contains("CodeGeneration", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Aspire.Hosting")]
    [InlineData("Aspire.Hosting@")]
    [InlineData("@13.5.0")]
    [InlineData("Aspire.Hosting@not-a-version")]
    [InlineData("Aspire@Hosting@13.5.0")]
    public async Task SdkExportWithMalformedPackageReturnsInvalidCommand(string package)
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(interactionService, out var workspace, out _);
        using var _2 = workspace;

        var exitCode = await InvokeAsync(provider, $"sdk export --language typescript --package \"{package}\"");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Fact]
    public async Task SdkExportWithMismatchedCoreVersionReturnsInvalidCommand()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(interactionService, out var workspace, out _);
        using var _2 = workspace;

        // The scanner loads the core assemblies this CLI ships with, so honouring a different core
        // version would export this CLI's surface under someone else's version number — the same
        // stale-signature problem this command exists to fix.
        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package Aspire.Hosting@1.0.0");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Fact]
    public async Task SdkExportAcceptsCoreVersionThatDiffersOnlyByBuildMetadata()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(interactionService, out var workspace, out _);
        using var _2 = workspace;

        var executionContext = provider.GetRequiredService<Aspire.Cli.CliExecutionContext>();

        var exitCode = await InvokeAsync(
            provider,
            $"sdk export --language typescript --package Aspire.Hosting@{executionContext.IdentitySdkVersion}+build.5");

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("13.5.*")]
    [InlineData("[13.5.0,14.0.0)")]
    [InlineData("13.5.0-*")]
    public async Task SdkExportWithFloatingVersionReturnsInvalidCommand(string version)
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(interactionService, out var workspace, out _);
        using var _2 = workspace;

        // Floating versions are rejected before restore rather than resolved, because a document
        // published under a range would silently describe a different SDK on the next restore.
        var exitCode = await InvokeAsync(provider, $"sdk export --language typescript --package \"Aspire.Hosting@{version}\"");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Fact]
    public async Task SdkExportForAPackageTheCheckoutWouldSubstituteReturnsInvalidCommand()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(
            interactionService,
            workspace,
            rpcClient,
            appHostServerProject,
            identityVersion: "13.5.0");
        appHostServerProject.AddLocalProjectSubstitution("Aspire.Hosting.Redis", "13.5.0");

        // A CLI running from a repository checkout builds first-party integrations from src/ and
        // throws the requested package version away, so honouring this would publish the 13.5.0
        // checkout's API surface under 13.4.0 — the mislabel this command exists to prevent. Both
        // halves are pinned rather than derived from the running assembly so the rejection turns on
        // the checkout-versus-request mismatch and not on whether this build carries a prerelease
        // label, which an official release build strips.
        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package Aspire.Hosting.Redis@13.4.0");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Null(rpcClient.LastExportRequest);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Fact]
    public async Task SdkExportForASubstitutedPackageAtTheCheckoutVersionSucceeds()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(interactionService, workspace, rpcClient, appHostServerProject);
        appHostServerProject.AddLocalProjectSubstitution("Aspire.Hosting.Redis", CheckoutVersionPrefix(provider));

        // Exporting the version the checkout actually contains is the local development case and
        // stays supported: the project reference and the label describe the same surface.
        var checkoutVersion = provider.GetRequiredService<Aspire.Cli.CliExecutionContext>().IdentitySdkVersion;

        var exitCode = await InvokeAsync(provider, $"sdk export --language typescript --package Aspire.Hosting.Redis@{checkoutVersion}");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(("typescript", "Aspire.Hosting.Redis", checkoutVersion), rpcClient.LastExportRequest);
    }

    /// <summary>
    /// The version this CLI reports is overrideable (<c>ASPIRE_CLI_VERSION</c>, the install sidecar),
    /// so comparing the request against it alone lets a caller name the checkout whatever they like.
    /// The version the checkout actually builds comes from the checkout itself and settles it.
    /// </summary>
    [Fact]
    public async Task SdkExportRejectsASubstitutedPackageWhenTheCheckoutBuildsADifferentVersion()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(
            interactionService,
            workspace,
            rpcClient,
            appHostServerProject,
            identityVersion: "99.0.0");
        appHostServerProject.AddLocalProjectSubstitution("Aspire.Hosting.Redis", "13.5.0");

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package Aspire.Hosting.Redis@99.0.0");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Null(rpcClient.LastExportRequest);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    /// <summary>
    /// An <c>ASPIRE_CLI_*</c> override makes the run an emulation of a build this checkout is not.
    /// The overrides stay useful everywhere else; they just cannot also decide the label on a
    /// document generated from local source.
    /// </summary>
    [Fact]
    public async Task SdkExportRejectsASubstitutedPackageWhenTheCliIdentityIsOverridden()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(
            interactionService,
            workspace,
            rpcClient,
            appHostServerProject,
            identityVersion: "13.5.0",
            identityOverridden: true,
            identityVersionForged: true);
        appHostServerProject.AddLocalProjectSubstitution("Aspire.Hosting.Redis", "13.5.0");

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package Aspire.Hosting.Redis@13.5.0");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Null(rpcClient.LastExportRequest);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    /// <summary>
    /// A normally installed CLI can export the core package, which is the command's advertised
    /// default invocation.
    /// </summary>
    /// <remarks>
    /// The guard used to test <c>IdentityOverridden</c>, an aggregate that is
    /// <see langword="true"/> whenever any identity field came from an environment variable
    /// <em>or the install sidecar</em>. Every install route writes a sidecar carrying channel and
    /// version, so the aggregate is set on ordinary installs and the guard rejected precisely the
    /// CLIs its own error message told callers to use. Only a version this run invented can make
    /// the label unverifiable, so that is what the guard tests.
    /// </remarks>
    [Fact]
    public async Task SdkExportOfTheCorePackageAcceptsASidecarSuppliedIdentity()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(
            interactionService,
            workspace,
            rpcClient,
            appHostServerProject,
            identityVersion: "13.5.0",
            identityOverridden: true,
            identityVersionForged: false);

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript");

        Assert.Equal(0, exitCode);
        var request = Assert.NotNull(rpcClient.LastExportRequest);
        Assert.Equal("Aspire.Hosting", request.PackageName);
    }

    /// <summary>
    /// When the checkout cannot say what it builds there is nothing left to check the label against,
    /// and an unverifiable label is the failure mode this command exists to prevent.
    /// </summary>
    [Fact]
    public async Task SdkExportRejectsASubstitutedPackageWhenTheCheckoutVersionIsUnknown()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(interactionService, workspace, rpcClient, appHostServerProject);
        appHostServerProject.AddLocalProjectSubstitution("Aspire.Hosting.Redis", checkoutVersionPrefix: null);

        var checkoutVersion = provider.GetRequiredService<Aspire.Cli.CliExecutionContext>().IdentitySdkVersion;

        var exitCode = await InvokeAsync(provider, $"sdk export --language typescript --package Aspire.Hosting.Redis@{checkoutVersion}");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Null(rpcClient.LastExportRequest);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    /// <summary>
    /// Neither scanner honours the requested SDK version — the repository scanner builds
    /// <c>src/Aspire.Hosting</c> and the prebuilt scanner loads the assemblies bundled with the CLI
    /// — so a core export always describes this CLI. The version guard that enforces that compares
    /// the request against the identity, which an override also controls, and the default request is
    /// that same identity, so the comparison is vacuous under an override.
    /// </summary>
    [Fact]
    public async Task SdkExportOfTheCorePackageRejectsAnOverriddenCliIdentity()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(
            interactionService,
            workspace,
            rpcClient,
            appHostServerProject,
            identityVersion: "99.0.0",
            identityOverridden: true,
            identityVersionForged: true);

        // No --package at all, so this is the default invocation: Aspire.Hosting at the identity
        // version. Without the guard this publishes the current core surface as 99.0.0.
        var exitCode = await InvokeAsync(provider, "sdk export --language typescript");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Null(rpcClient.LastExportRequest);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    /// <summary>
    /// Repository mode is entered through <c>ASPIRE_REPO_ROOT</c>, which is not an identity field,
    /// so an installed CLI pointed at a checkout on another version line has an entirely honest
    /// identity and no override in effect. The generated scanner always project-references
    /// <c>src/Aspire.Hosting</c>, so what it exports is the checkout's core surface under the
    /// installed CLI's number.
    /// </summary>
    [Fact]
    public async Task SdkExportOfTheCorePackageRejectsACheckoutOnAnotherVersionLine()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(
            interactionService,
            workspace,
            rpcClient,
            appHostServerProject,
            identityVersion: "13.4.0");
        appHostServerProject.AddLocalProjectSubstitution("Aspire.Hosting", "13.5.0");

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Null(rpcClient.LastExportRequest);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    /// <summary>
    /// The core package is matched case-insensitively but resolved through the filesystem, and the
    /// generated scanner project-references <c>src/Aspire.Hosting</c> under that exact spelling
    /// regardless of how the caller spelled it. A lookup under the caller's spelling would miss on
    /// a case-sensitive filesystem and skip the check while the scanner still built the checkout.
    /// </summary>
    [Fact]
    public async Task SdkExportOfTheCorePackageRejectsACheckoutOnAnotherVersionLineWhateverTheCasing()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(
            interactionService,
            workspace,
            rpcClient,
            appHostServerProject,
            identityVersion: "13.4.0");
        appHostServerProject.AddLocalProjectSubstitution("Aspire.Hosting", "13.5.0");

        // The version has to match the identity or the earlier core guard rejects it for a different
        // reason, which would hide whether the substitution lookup found anything.
        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package aspire.hosting@13.4.0");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Null(rpcClient.LastExportRequest);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    /// <summary>
    /// Rejecting a bad request under any spelling is half of it. The exported document records the
    /// package name verbatim as the identity documentation is keyed on, and the scanner builds
    /// <c>src/Aspire.Hosting</c> whatever was typed, so a good request has to be published under the
    /// canonical id rather than the caller's spelling.
    /// </summary>
    [Fact]
    public async Task SdkExportOfTheCorePackageIsPublishedUnderItsCanonicalNameWhateverTheCasing()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(
            interactionService,
            workspace,
            rpcClient,
            appHostServerProject,
            identityVersion: "13.5.0");
        appHostServerProject.AddLocalProjectSubstitution("Aspire.Hosting", "13.5.0");

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package aspire.hosting@13.5.0");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(("typescript", "Aspire.Hosting", "13.5.0"), rpcClient.LastExportRequest);

        var stdout = Assert.Single(interactionService.DisplayedRawText, entry => entry.ConsoleOverride == ConsoleOutput.Standard);
        using var document = JsonDocument.Parse(stdout.Text);
        Assert.Equal("Aspire.Hosting", document.RootElement.GetProperty("package").GetProperty("name").GetString());
    }

    /// <summary>
    /// The same checkout on the same version line is exactly what the label claims, so it exports.
    /// </summary>
    [Fact]
    public async Task SdkExportOfTheCorePackageFromAMatchingCheckoutSucceeds()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(
            interactionService,
            workspace,
            rpcClient,
            appHostServerProject,
            identityVersion: "13.5.0");
        appHostServerProject.AddLocalProjectSubstitution("Aspire.Hosting", "13.5.0");

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("Aspire.Hosting", rpcClient.LastExportRequest?.PackageName);
        Assert.Equal("13.5.0", rpcClient.LastExportRequest?.PackageVersion);
    }

    [Fact]
    public async Task SdkExportForAThirdPartyPackageIsUnaffectedByCheckoutSubstitution()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        var rpcClient = new StubExportRpcClient();
        using var provider = CreateProvider(interactionService, workspace, rpcClient, appHostServerProject);
        appHostServerProject.AddLocalProjectSubstitution("Aspire.Hosting.Redis", CheckoutVersionPrefix(provider));

        // A Community Toolkit integration is never replaced by a repository project, so it restores
        // at the requested version even from a checkout and must keep exporting.
        var exitCode = await InvokeAsync(
            provider,
            "sdk export --language typescript --package CommunityToolkit.Aspire.Hosting.ActiveMQ@13.4.0");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(("typescript", "CommunityToolkit.Aspire.Hosting.ActiveMQ", "13.4.0"), rpcClient.LastExportRequest);
    }

    /// <summary>
    /// A bare NuGet version is a minimum, not an equality, so a package that is missing from the feed
    /// restores as the next one up and the export is published under a version it does not describe.
    /// Only the requested package is pinned; the code generation package tracks this CLI and is
    /// resolved the same way <c>sdk generate</c> resolves it.
    /// </summary>
    [Fact]
    public async Task SdkExportPinsOnlyTheRequestedPackageToAnExactVersion()
    {
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostServerProject = new CapturingAppHostServerProject(workspace.WorkspaceRoot.FullName);
        using var provider = CreateProvider(interactionService, workspace, new StubExportRpcClient(), appHostServerProject);

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package Aspire.Hosting.Redis@13.5.0");

        Assert.Equal(CliExitCodes.Success, exitCode);

        var requested = Assert.Single(appHostServerProject.Integrations, integration => integration.Name == "Aspire.Hosting.Redis");
        Assert.True(requested.RequireExactVersion);

        var codeGeneration = Assert.Single(
            appHostServerProject.Integrations,
            integration => integration.Name.Contains("CodeGeneration", StringComparison.OrdinalIgnoreCase));
        Assert.False(codeGeneration.RequireExactVersion);
    }

    [Fact]
    public async Task SdkExportWithUnsupportedLanguageReturnsInvalidCommand()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(interactionService, out var workspace, out _, new ThrowingExportRpcClient(
            new NotSupportedException("The 'Go' code generator does not implement IApiReferenceExporter.")));
        using var _2 = workspace;

        var exitCode = await InvokeAsync(provider, "sdk export --language go --package Aspire.Hosting@13.5.0");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Fact]
    public async Task SdkExportWhenRpcFailsReturnsFailureAndWritesNothingToStdout()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(interactionService, out var workspace, out _, new ThrowingExportRpcClient(
            new RemoteInvocationException("apphost blew up", 0, errorData: null)));
        using var _2 = workspace;

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript --package Aspire.Hosting@13.5.0");

        Assert.NotEqual(CliExitCodes.Success, exitCode);

        // A partial document is worse than none: a consumer would publish it as if it were complete.
        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Fact]
    public async Task SdkDumpJsonPayloadIsUnchangedByTheSharedPreparationExtraction()
    {
        // sdk export and sdk dump now share preparation code but nothing else. This lives beside the
        // export tests because it guards the extraction, not dump's own behaviour: dump must keep
        // producing its existing capabilities payload and must not be routed through the canonical
        // exporter.
        var interactionService = new TestInteractionService();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var rpcClient = new CapabilitiesRpcClient();
        using var provider = CreateProvider(
            interactionService,
            workspace,
            rpcClient,
            new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName));

        var exitCode = await InvokeAsync(provider, "sdk dump --format json Aspire.Hosting.Redis@13.5.0");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(["Aspire.Hosting.Redis"], rpcClient.LastAssemblyNames);

        var stdout = Assert.Single(interactionService.DisplayedRawText);
        using var document = JsonDocument.Parse(stdout.Text);

        // The capabilities shape, not the canonical export schema.
        Assert.False(document.RootElement.TryGetProperty("schemaVersion", out _));
        Assert.True(document.RootElement.TryGetProperty("Capabilities", out _));
    }

    private static async Task<int> InvokeAsync(ServiceProvider provider, string commandLine)
    {
        var command = provider.GetRequiredService<RootCommand>();
        return await command.Parse(commandLine).InvokeAsync().DefaultTimeout();
    }

    private ServiceProvider CreateProvider(
        TestInteractionService interactionService,
        out TemporaryWorkspace workspace,
        out StubExportRpcClient rpcClient,
        IAppHostRpcClient? overrideRpcClient = null)
    {
        workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        rpcClient = new StubExportRpcClient();
        return CreateProvider(
            interactionService,
            workspace,
            overrideRpcClient ?? rpcClient,
            new FakeSucceedingAppHostServerProject(workspace.WorkspaceRoot.FullName));
    }

    private ServiceProvider CreateProvider(
        TestInteractionService interactionService,
        TemporaryWorkspace workspace,
        IAppHostRpcClient rpcClient,
        IAppHostServerProject appHostServerProject,
        string? identityVersion = null,
        bool identityOverridden = false,
        bool identityVersionForged = false)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            if (identityVersion is not null || identityOverridden || identityVersionForged)
            {
                options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(
                    workspace.WorkspaceRoot,
                    identityVersion: identityVersion,
                    identityOverridden: identityOverridden,
                    identityVersionForged: identityVersionForged);
            }
        });

        services.AddSingleton<IAppHostServerProjectFactory>(new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (_, _) => Task.FromResult(appHostServerProject)
        });
        services.AddSingleton<IAppHostServerSessionFactory>(new FakeAppHostServerSessionFactory
        {
            Session = new FakeAppHostServerSession(rpcClient)
        });

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The <c>Major.Minor.Patch</c> a checkout matching this CLI's identity would build. Tests that
    /// exercise the honest local-development path need the substitution to agree with the identity.
    /// </summary>
    private static string CheckoutVersionPrefix(ServiceProvider provider)
    {
        var identity = SemVersion.Parse(
            provider.GetRequiredService<Aspire.Cli.CliExecutionContext>().IdentitySdkVersion,
            SemVersionStyles.Any);

        return $"{identity.Major}.{identity.Minor}.{identity.Patch}";
    }

    private sealed class StubExportRpcClient : FakeAppHostRpcClient
    {
        public (string Language, string PackageName, string PackageVersion)? LastExportRequest { get; private set; }

        public override Task<JsonElement> ExportApiAsync(string languageId, string packageName, string packageVersion, CancellationToken cancellationToken)
        {
            LastExportRequest = (languageId, packageName, packageVersion);

            using var document = JsonDocument.Parse($$"""
                {
                  "schemaVersion": 1,
                  "language": "{{languageId}}",
                  "package": { "name": "{{packageName}}", "version": "{{packageVersion}}" },
                  "modules": [],
                  "declarations": []
                }
                """);

            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class ThrowingExportRpcClient(Exception exception) : FakeAppHostRpcClient
    {
        public override Task<JsonElement> ExportApiAsync(string languageId, string packageName, string packageVersion, CancellationToken cancellationToken)
            => Task.FromException<JsonElement>(exception);
    }

    private sealed class CapabilitiesRpcClient : FakeAppHostRpcClient
    {
        public IReadOnlyList<string>? LastAssemblyNames { get; private set; }

        public override Task<CapabilitiesInfo> GetCapabilitiesForAssembliesAsync(IReadOnlyList<string> assemblyNames, CancellationToken cancellationToken)
        {
            LastAssemblyNames = assemblyNames;
            return Task.FromResult(new CapabilitiesInfo());
        }
    }

    private sealed class CapturingAppHostServerProject(string appDirectoryPath) : IAppHostServerProject
    {
        public string AppDirectoryPath { get; } = appDirectoryPath;

        public string? PackageSourceOverride { get; private set; }

        public IReadOnlyList<IntegrationReference> Integrations { get; private set; } = [];

        public string GetInstanceIdentifier() => AppDirectoryPath;

        public Task<AppHostServerPrepareResult> PrepareAsync(
            string sdkVersion,
            IEnumerable<IntegrationReference> integrations,
            string? requestedChannel = null,
            string? packageSourceOverride = null,
            CancellationToken cancellationToken = default)
        {
            PackageSourceOverride = packageSourceOverride;
            Integrations = [.. integrations];
            return Task.FromResult(new AppHostServerPrepareResult(Success: true, Output: null));
        }

        public Task<AppHostServerRunResult> RunAsync(
            int hostPid,
            IReadOnlyDictionary<string, string>? environmentVariables,
            string[]? additionalArgs,
            bool debug,
            AppHostServerRunControl? runControl)
            => throw new NotSupportedException("Run should not be invoked when using a fake codegen session.");
    }
}
