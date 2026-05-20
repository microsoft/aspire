// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI doctor command, specifically testing
/// certificate trust level detection on Linux.
/// </summary>
public sealed class DoctorCommandTests(ITestOutputHelper output)
{
    public static TheoryData<string> AlternativeToolchains => new()
    {
        "bun",
        "yarn",
        "pnpm"
    };

    [Fact]
    public async Task DoctorCommand_WithoutSslCertDir_ShowsPartiallyTrusted()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // INTENTIONAL TEST FAILURE: Validate that cast recording content appears in GitHub step summary
        Assert.Fail("Intentional failure to test cast recording in step summary (DoctorCommand_WithoutSslCertDir)");

        // Generate and trust dev certs inside the container (Docker images don't have them by default)
        await auto.TypeAsync("dotnet dev-certs https --trust 2>/dev/null || dotnet dev-certs https");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Unset SSL_CERT_DIR to trigger partial trust detection on Linux
        await auto.TypeAsync("unset SSL_CERT_DIR");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire doctor");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("dev-certs") && s.ContainsText("partially trusted"),
            timeout: TimeSpan.FromSeconds(60), description: "doctor to complete with partial trust warning");
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task DoctorCommand_WithSslCertDir_ShowsTrusted()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // INTENTIONAL TEST FAILURE: Validate that cast recording content appears in GitHub step summary
        Assert.Fail("Intentional failure to test cast recording in step summary (DoctorCommand_WithSslCertDir)");

        // Generate and trust dev certs inside the container (Docker images don't have them by default)
        await auto.TypeAsync("dotnet dev-certs https --trust 2>/dev/null || dotnet dev-certs https");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Set SSL_CERT_DIR to include dev-certs trust path for full trust
        await auto.TypeAsync("export SSL_CERT_DIR=\"/etc/ssl/certs:$HOME/.aspnet/dev-certs/trust\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire doctor");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
        {
            // Wait for doctor to complete
            if (!s.ContainsText("dev-certs"))
            {
                return false;
            }

            // Fail if we see partial trust when SSL_CERT_DIR is configured
            if (s.ContainsText("partially trusted"))
            {
                throw new InvalidOperationException(
                    "Unexpected 'partially trusted' message when SSL_CERT_DIR is configured!");
            }

            return s.ContainsText("certificate is trusted");
        }, timeout: TimeSpan.FromSeconds(60), description: "doctor to complete with trusted certificate");
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Theory]
    [MemberData(nameof(AlternativeToolchains))]
    [CaptureWorkspaceOnFailure]
    public async Task DoctorCommand_TypeScriptAppHostReportsMissingConfiguredToolchain(string toolchain)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        output.WriteLine($"Testing aspire doctor missing-tool detection for: {toolchain}");

        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        TypeScriptAppHostToolchainTestHelpers.SetPackageManager(workspace.WorkspaceRoot.FullName, toolchain, cleanInstallState: true);

        // Verify the configured toolchain can start and stop the generated AppHost
        // before doctor is asked to report that the toolchain is missing from PATH.
        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("""mkdir -p ./doctor-path && ln -sf "$(command -v aspire)" ./doctor-path/aspire && ln -sf "$(command -v dotnet)" ./doctor-path/dotnet && if command -v docker >/dev/null 2>&1; then ln -sf "$(command -v docker)" ./doctor-path/docker; fi && export PATH="$PWD/doctor-path" """);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire doctor");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText($"TypeScript AppHost requires '{toolchain}'.") &&
                 s.ContainsText($"Install {TypeScriptAppHostToolchainTestHelpers.GetDisplayName(toolchain)} tooling and rerun 'aspire doctor'.") &&
                 s.ContainsText(TypeScriptAppHostToolchainTestHelpers.GetInstallationLink(toolchain)),
            timeout: TimeSpan.FromSeconds(60),
            description: $"doctor to report missing {toolchain} tooling");
        await auto.WaitForAnyPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
