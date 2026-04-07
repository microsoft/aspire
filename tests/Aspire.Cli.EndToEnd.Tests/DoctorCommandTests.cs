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
    [Fact]
    public async Task DoctorCommand_WithoutSslCertDir_ShowsPartiallyTrusted()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

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
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

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
}
