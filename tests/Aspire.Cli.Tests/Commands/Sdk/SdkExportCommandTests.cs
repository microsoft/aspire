// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Aspire.Cli.Tests.Commands.Sdk;

public class SdkExportCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task SdkExportWithHelpReturnsZero()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var exitCode = await InvokeAsync(provider, "sdk export --help");

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("typescript/nodejs")]
    [InlineData("typescript")]
    [InlineData("TypeScript")]
    public async Task SdkExportSendsTheResolvedGeneratorName(string language)
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            out var rpcClient,
            out _);
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(provider, $"sdk export --language {language}");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal("TypeScript", Assert.NotNull(rpcClient.LastExportRequest).Language);
    }

    [Fact]
    public async Task SdkExportRestoresExactPackageAndWritesOnlyJsonToStdout()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            out var rpcClient,
            out var project);
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(
            provider,
            "sdk export --language typescript --package Contoso.Aspire.Widgets@2.0");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(("TypeScript", "Contoso.Aspire.Widgets", "2.0.0"), rpcClient.LastExportRequest);

        var package = Assert.Single(
            project.Integrations,
            integration => integration.Name == "Contoso.Aspire.Widgets");
        Assert.Equal("[2.0.0]", package.Version);
        Assert.True(package.DisableLocalProjectSubstitution);

        var generator = Assert.Single(
            project.Integrations,
            integration => integration.Name.Contains("CodeGeneration", StringComparison.OrdinalIgnoreCase));
        var cliVersion = provider.GetRequiredService<CliExecutionContext>().IdentityVersion;
        Assert.Equal(cliVersion, generator.Version);

        Assert.Equal(ConsoleOutput.Error, interactionService.Console);
        var stdout = Assert.Single(
            interactionService.DisplayedRawText,
            entry => entry.ConsoleOverride == ConsoleOutput.Standard);
        Assert.DoesNotContain('\r', stdout.Text);

        using var document = JsonDocument.Parse(stdout.Text);
        Assert.Equal("Contoso.Aspire.Widgets", document.RootElement.GetProperty("package").GetProperty("name").GetString());
        Assert.DoesNotContain(
            interactionService.DisplayedMessages,
            message => (message.ConsoleOverride ?? interactionService.Console) == ConsoleOutput.Standard);
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3.0", "1.2.3")]
    [InlineData("1.0.0.0-beta", "1.0.0-beta")]
    [InlineData("1.2.3.4-preview.1+meta", "1.2.3.4-preview.1")]
    public async Task SdkExportRestoresNormalizedFourPartNuGetVersion(string requestedVersion, string normalizedVersion)
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            out var rpcClient,
            out var project);
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(
            provider,
            $"sdk export --language typescript --package Contoso.Aspire.Widgets@{requestedVersion}");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(("TypeScript", "Contoso.Aspire.Widgets", normalizedVersion), rpcClient.LastExportRequest);

        var package = Assert.Single(
            project.Integrations,
            integration => integration.Name == "Contoso.Aspire.Widgets");
        Assert.Equal($"[{normalizedVersion}]", package.Version);
    }

    [Fact]
    public async Task SdkExportRejectsRequestedGeneratorPackageAtDifferentVersion()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            out var rpcClient,
            out var project,
            identityVersion: "13.5.0");
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(
            provider,
            "sdk export --language typescript --package Aspire.Hosting.CodeGeneration.TypeScript@13.4.0");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, project.PrepareCallCount);
        Assert.Null(rpcClient.LastExportRequest);
        Assert.Equal(
            "SDK API export cannot export Aspire.Hosting.CodeGeneration.TypeScript because that package supplies the selected language's code generator instead of an integration API surface.",
            Assert.Single(interactionService.DisplayedErrors));
    }

    [Fact]
    public async Task SdkExportRejectsRequestedGeneratorPackageAtCliVersion()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            out var rpcClient,
            out var project,
            identityVersion: "13.5.0");
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(
            provider,
            "sdk export --language typescript --package Aspire.Hosting.CodeGeneration.TypeScript@13.5.0.0");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, project.PrepareCallCount);
        Assert.Null(rpcClient.LastExportRequest);
        Assert.Equal(
            "SDK API export cannot export Aspire.Hosting.CodeGeneration.TypeScript because that package supplies the selected language's code generator instead of an integration API surface.",
            Assert.Single(interactionService.DisplayedErrors));
    }

    [Fact]
    public async Task SdkExportDefaultsToCoreAtTheRunningSdkVersion()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            out var rpcClient,
            out var project);
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript");

        Assert.Equal(CliExitCodes.Success, exitCode);
        var expectedVersion = provider.GetRequiredService<CliExecutionContext>().IdentitySdkVersion;
        Assert.Equal(("TypeScript", "Aspire.Hosting", expectedVersion), rpcClient.LastExportRequest);
        Assert.DoesNotContain(project.Integrations, integration => integration.Name == "Aspire.Hosting");
    }

    [Theory]
    [InlineData("Aspire.Hosting")]
    [InlineData("Aspire.Hosting@")]
    [InlineData("@13.5.0")]
    [InlineData(" @13.5.0")]
    [InlineData("Aspire@Hosting@13.5.0")]
    [InlineData("Contoso@not-a-version")]
    [InlineData("Contoso@13.5.*")]
    [InlineData("Contoso@[13.5.0]")]
    [InlineData("Contoso@1.2.3.4-")]
    [InlineData("Contoso@1.2.3.4+")]
    [InlineData("Contoso@1.2.3.4-preview..1")]
    public async Task SdkExportRejectsMalformedOrNonExactPackages(string package)
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            out var rpcClient,
            out _);
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(provider, $"sdk export --language typescript --package \"{package}\"");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Null(rpcClient.LastExportRequest);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Fact]
    public async Task SdkExportRejectsCoreVersionDifferentFromTheCli()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            out var rpcClient,
            out _);
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(
            provider,
            "sdk export --language typescript --package Aspire.Hosting@0.0.1");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Null(rpcClient.LastExportRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkExportRejectsCoreWhenEmulatedVersionDiffersFromTheRunningBinary(bool specifyPackage)
    {
        var interactionService = new TestInteractionService();
        var physicalSdkVersion = VersionHelper.GetDefaultSdkVersion();
        var emulatedSdkVersion = physicalSdkVersion == "0.0.1" ? "0.0.2" : "0.0.1";
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            out var rpcClient,
            out _,
            identityVersion: emulatedSdkVersion);
        using var workspaceLease = workspace;

        var command = specifyPackage
            ? $"sdk export --language typescript --package Aspire.Hosting@{emulatedSdkVersion}"
            : "sdk export --language typescript";
        var exitCode = await InvokeAsync(provider, command);

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Null(rpcClient.LastExportRequest);
    }

    [Fact]
    public async Task SdkExportUsesStructuredInvalidParametersForUnsupportedLanguage()
    {
        var interactionService = new TestInteractionService();
        var rpcClient = new ThrowingExportRpcClient(new RemoteInvocationException(
            "No code generator found for language: klingon.",
            (int)JsonRpcErrorCode.InvalidParams,
            errorData: null));
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            rpcClient,
            new CapturingAppHostServerProject());
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(provider, "sdk export --language klingon");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(interactionService.DisplayedRawText);
        Assert.Contains(
            interactionService.DisplayedErrors,
            error => error.Contains("klingon", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SdkExportRejectsLanguageWithoutCodeGeneratorBeforePreparation()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            out var rpcClient,
            out var project);
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(provider, "sdk export --language csharp");

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Collection(
            interactionService.DisplayedErrors,
            error => Assert.Equal(
                "SDK API export is not supported for C# (.NET) because it does not use a code generator.",
                error));
        Assert.Equal(0, project.PrepareCallCount);
        Assert.Null(rpcClient.LastExportRequest);
    }

    [Fact]
    public async Task SdkExportRpcFailureWritesNoPartialDocument()
    {
        var interactionService = new TestInteractionService();
        var rpcClient = new ThrowingExportRpcClient(
            new RemoteInvocationException("AppHost export failed.", 0, errorData: null));
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            rpcClient,
            new CapturingAppHostServerProject());
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(provider, "sdk export --language typescript");

        Assert.Equal(CliExitCodes.FailedToBuildArtifacts, exitCode);
        Assert.Empty(interactionService.DisplayedRawText);
    }

    [Fact]
    public async Task SdkExportHasNoSourceOrOutputOptions()
    {
        var interactionService = new TestInteractionService();
        using var provider = CreateProvider(
            interactionService,
            out var workspace,
            out var rpcClient,
            out _);
        using var workspaceLease = workspace;

        var exitCode = await InvokeAsync(
            provider,
            "sdk export --language typescript --source custom-feed");

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.Null(rpcClient.LastExportRequest);
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
        out CapturingAppHostServerProject project,
        string? identityVersion = null)
    {
        workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        rpcClient = new StubExportRpcClient();
        project = new CapturingAppHostServerProject();
        return CreateProvider(interactionService, out _, rpcClient, project, workspace, identityVersion);
    }

    private ServiceProvider CreateProvider(
        TestInteractionService interactionService,
        out TemporaryWorkspace workspace,
        IAppHostRpcClient rpcClient,
        IAppHostServerProject appHostServerProject,
        TemporaryWorkspace? existingWorkspace = null,
        string? identityVersion = null)
    {
        workspace = existingWorkspace ?? TemporaryWorkspace.CreateForCli(outputHelper);
        var testWorkspace = workspace;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            if (identityVersion is not null)
            {
                options.CliExecutionContextFactory = _ => testWorkspace.CreateExecutionContext(
                    identityVersion: identityVersion,
                    identityOverridden: true);
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

    private sealed class StubExportRpcClient : FakeAppHostRpcClient
    {
        public (string Language, string PackageName, string PackageVersion)? LastExportRequest { get; private set; }

        public override Task<JsonElement> ExportApiAsync(
            string languageId,
            string packageName,
            string packageVersion,
            CancellationToken cancellationToken)
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
                """.ReplaceLineEndings("\r\n"));

            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class ThrowingExportRpcClient(Exception exception) : FakeAppHostRpcClient
    {
        public override Task<JsonElement> ExportApiAsync(
            string languageId,
            string packageName,
            string packageVersion,
            CancellationToken cancellationToken)
            => Task.FromException<JsonElement>(exception);
    }

    private sealed class CapturingAppHostServerProject : IAppHostServerProject
    {
        public string AppDirectoryPath => Environment.CurrentDirectory;

        public IReadOnlyList<IntegrationReference> Integrations { get; private set; } = [];

        public int PrepareCallCount { get; private set; }

        public string GetInstanceIdentifier() => AppDirectoryPath;

        public Task<AppHostServerPrepareResult> PrepareAsync(
            string sdkVersion,
            IEnumerable<IntegrationReference> integrations,
            string? requestedChannel = null,
            string? packageSourceOverride = null,
            CancellationToken cancellationToken = default)
        {
            PrepareCallCount++;
            Integrations = [.. integrations];
            return Task.FromResult(new AppHostServerPrepareResult(Success: true, Output: null));
        }

        public Task<AppHostServerRunResult> RunAsync(
            int hostPid,
            IReadOnlyDictionary<string, string>? environmentVariables,
            string[]? additionalArgs,
            bool debug,
            AppHostServerRunControl? runControl)
            => throw new NotSupportedException("Run should not be invoked by this test.");
    }
}
