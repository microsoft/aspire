// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Azure.Core;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AcaExpressDeploymentTests(ITestOutputHelper output)
{
    private const string ApiVersion = "2026-03-02-preview";
    private const string ProjectName = "AcaExpress";
    private const string ApiMessage = "express-api-response";
    private const string DeploymentLogFile = "deployment.txt";
    // Leave room for independent Azure cleanup before the project's 90-minute hang timeout.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task DeployPublicHttpGraphWithManualSecrets()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Deployment terminal automation requires Linux.");

        var subscriptionId = AzureAuthenticationHelpers.TryGetSubscriptionId();
        if (string.IsNullOrEmpty(subscriptionId))
        {
            Assert.Skip("Azure subscription not configured. Set ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION.");
        }

        if (!AzureAuthenticationHelpers.IsAzureAuthAvailable())
        {
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                Assert.Fail("Azure authentication not available in CI. Check OIDC configuration.");
            }

            Assert.Skip("Azure authentication not available. Run 'az login' to authenticate.");
        }

        var strategy = DeploymentE2ETestHelpers.GetCurrentBuildCliInstallStrategy();
        Assert.SkipUnless(
            strategy.Mode is CliInstallMode.Preinstalled or CliInstallMode.LocalHive or CliInstallMode.LocalArchive or CliInstallMode.PullRequest,
            "This experimental scenario requires current-build artifacts. Set ASPIRE_E2E_ARCHIVE for a local run.");

        using var timeout = new CancellationTokenSource(s_testTimeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var cancellationToken = cancellation.Token;
        using var workspace = TemporaryWorkspace.Create(output);
        using var managementClient = new HttpClient();
        var credential = AzureAuthenticationHelpers.GetAzureCredential();
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName($"aca-express-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(workspace.Path, ProjectName);
        var startTime = DateTime.UtcNow;
        var deploymentUrls = new Dictionary<string, string>();
        var deploymentAttempted = false;
        Exception? deploymentFailure = null;

        // Refuse to take ownership of an existing group, even if a test name unexpectedly collides.
        Assert.Equal("false", (await RunAzureCliAsync(
            ["group", "exists", "--subscription", subscriptionId, "--name", resourceGroupName],
            cancellationToken)).Trim());

        try
        {
            using var terminalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal(width: 320);
            var pendingRun = terminal.RunAsync(terminalCancellation.Token);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            try
            {
                await auto.PrepareEnvironmentAsync(workspace, counter);
                await auto.InstallAspireCliAsync(strategy, counter, output);
                await auto.AspireNewAsync(ProjectName, counter, template: AspireTemplate.EmptyAppHost);
                await auto.RunCommandAsync($"cd {ProjectName}", counter);
                await auto.TypeAsync("aspire add Aspire.Hosting.Azure.AppContainers");
                await auto.EnterAsync();
                await auto.WaitForAspireAddCompletionAsync(counter);
                await auto.RunCommandAsync(
                    "dotnet new web -n Api --no-restore && dotnet new web -n Frontend --no-restore",
                    counter,
                    TimeSpan.FromMinutes(2));
                WriteApplication(projectDirectory);

                // The secret is created inside command substitution, never typed into the recorded
                // terminal, written into source, or printed. Both apps receive it as a secure parameter.
                // Do not capture the full workspace: deployment state can contain sensitive values.
                await auto.RunCommandAsync(
                    "set +x && set -o pipefail && unset ASPIRE_PLAYGROUND Azure__Location Azure__ResourceGroup Azure__SubscriptionId && " +
                    $"export AZURE__LOCATION=westus3 AZURE__RESOURCEGROUP={AspireCliShellCommandHelpers.QuoteBashArg(resourceGroupName)} " +
                    $"AZURE__SUBSCRIPTIONID={AspireCliShellCommandHelpers.QuoteBashArg(subscriptionId)} COLUMNS=320 && " +
                    "export Parameters__manualsecret=$(python3 -c 'import secrets; print(secrets.token_hex(32))') && " +
                    "test -n \"$Parameters__manualsecret\"",
                    counter);

                deploymentAttempted = true;
                await DeployAsync(auto, counter);
                var urls = await VerifyProviderConfigurationAsync(
                    managementClient, credential, subscriptionId, resourceGroupName, cancellationToken);
                VerifyDeploymentSummary(projectDirectory, urls);
                await VerifyPublicCallAsync(urls, cancellationToken);

                foreach (var (name, uri) in urls)
                {
                    deploymentUrls.Add(name, uri.AbsoluteUri);
                }

                await auto.RunCommandAsync("unset Parameters__manualsecret", counter);
                await auto.TypeAsync("exit");
                await auto.EnterAsync();
                await pendingRun.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            finally
            {
                // The deployment project does not link the CLI-only TerminalRun helper. Stop and
                // observe its shared terminal before deleting Azure resources, including on timeout.
                await terminalCancellation.CancelAsync();
                try
                {
                    await pendingRun.WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (OperationCanceledException) when (terminalCancellation.IsCancellationRequested)
                {
                    // Cancellation is the expected terminal shutdown path after a failed assertion.
                }
            }
        }
        catch (Exception ex)
        {
            deploymentFailure = ex;
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployPublicHttpGraphWithManualSecrets), resourceGroupName, ex.Message);
            throw;
        }
        finally
        {
            if (deploymentAttempted)
            {
                try
                {
                    await CleanupResourceGroupAsync(subscriptionId, resourceGroupName);
                    DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true);
                }
                catch (Exception ex)
                {
                    DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, ex.Message);
                    if (deploymentFailure is not null)
                    {
                        throw new AggregateException("Deployment and resource group cleanup both failed.", deploymentFailure, ex);
                    }

                    throw;
                }
            }
        }

        DeploymentReporter.ReportDeploymentSuccess(
            nameof(DeployPublicHttpGraphWithManualSecrets),
            resourceGroupName,
            deploymentUrls,
            DateTime.UtcNow - startTime);
    }

    private static async Task DeployAsync(Hex1bTerminalAutomator auto, SequenceCounter counter)
    {
        // pipefail is enabled in the terminal so tee cannot hide a failed deployment.
        await auto.TypeAsync($"aspire deploy 2>&1 | tee {DeploymentLogFile}");
        await auto.EnterAsync();
        await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));
    }

    private static void WriteApplication(string projectDirectory)
    {
        var appHostPath = Path.Combine(projectDirectory, "apphost.cs");
        var directives = File.ReadLines(appHostPath).Where(line => line.StartsWith("#:", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(directives);
        File.WriteAllText(appHostPath, $$"""
            {{string.Join(Environment.NewLine, directives)}}

            #pragma warning disable ASPIREACAEXPRESS001

            var builder = DistributedApplication.CreateBuilder(args);
            builder.AddAzureContainerAppEnvironment("env").AsExpress();
            var secret = builder.AddParameter("manualsecret", secret: true);

            var api = builder.AddProject("api", "Api/Api.csproj")
                .WithHttpEndpoint(targetPort: 8080)
                .WithExternalHttpEndpoints()
                .WithEnvironment("MANUAL_SECRET", secret);

            builder.AddProject("frontend", "Frontend/Frontend.csproj")
                .WithHttpEndpoint(targetPort: 8080)
                .WithExternalHttpEndpoints()
                .WithEnvironment("MANUAL_SECRET", secret)
                .WithReference(api);

            builder.Build().Run();
            """);

        // Explicit HTTP endpoints avoid a template's HTTPS launch profile creating extra ingress
        // ports. 8080 is a container target port, not a fixed local listening port.
        File.Delete(Path.Combine(projectDirectory, "Api", "Properties", "launchSettings.json"));
        File.Delete(Path.Combine(projectDirectory, "Frontend", "Properties", "launchSettings.json"));
        File.WriteAllText(Path.Combine(projectDirectory, "Api", "Program.cs"), $$"""
            var builder = WebApplication.CreateBuilder(args);
            var secret = builder.Configuration["MANUAL_SECRET"]
                ?? throw new InvalidOperationException("Manual secret is missing.");
            var app = builder.Build();
            app.MapGet("/", (HttpRequest request) =>
            {
                if (!string.Equals(request.Headers["X-Express-Test-Secret"], secret, StringComparison.Ordinal))
                {
                    return Results.Unauthorized();
                }

                return Results.Text("{{ApiMessage}}");
            });
            app.Run();
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "Frontend", "Program.cs"), """
            var builder = WebApplication.CreateBuilder(args);
            var secret = builder.Configuration["MANUAL_SECRET"]
                ?? throw new InvalidOperationException("Manual secret is missing.");
            var api = new Uri(builder.Configuration["services:api:http:0"]
                ?? throw new InvalidOperationException("The API service reference is missing."));
            if (api.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("The deployed API reference must use HTTPS.");
            }

            using var client = new HttpClient { BaseAddress = api, Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Add("X-Express-Test-Secret", secret);
            var app = builder.Build();
            app.MapGet("/", async (CancellationToken cancellationToken) =>
            {
                var message = await client.GetStringAsync("/", cancellationToken);
                return Results.Json(new { backend = api.GetLeftPart(UriPartial.Authority), message });
            });
            app.Run();
            """);
    }

    private static async Task<Dictionary<string, Uri>> VerifyProviderConfigurationAsync(
        HttpClient client, TokenCredential credential, string subscriptionId, string resourceGroupName, CancellationToken cancellationToken)
    {
        var resourcePath = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.App";
        using var environmentDocument = await GetAzureResourceAsync(
            client, credential, $"{resourcePath}/managedEnvironments?api-version={ApiVersion}", cancellationToken);
        var environment = Assert.Single(environmentDocument.RootElement.GetProperty("value").EnumerateArray());
        Assert.Equal("Express", environment.GetProperty("properties").GetProperty("environmentMode").GetString());
        var environmentId = environment.GetProperty("id").GetString();

        using var appsDocument = await GetAzureResourceAsync(
            client, credential, $"{resourcePath}/containerApps?api-version={ApiVersion}", cancellationToken);
        var apps = appsDocument.RootElement.GetProperty("value").EnumerateArray().ToArray();
        Assert.Equal(2, apps.Length);
        var urls = new Dictionary<string, Uri>();
        foreach (var app in apps)
        {
            var properties = app.GetProperty("properties");
            Assert.Equal(environmentId, properties.GetProperty("environmentId").GetString(), ignoreCase: true);
            var configuration = properties.GetProperty("configuration");
            var ingress = configuration.GetProperty("ingress");
            Assert.True(ingress.GetProperty("external").GetBoolean());
            Assert.False(ingress.GetProperty("allowInsecure").GetBoolean());
            Assert.Equal(8080, ingress.GetProperty("targetPort").GetInt32());
            var hostname = ingress.GetProperty("fqdn").GetString();
            Assert.Equal(UriHostNameType.Dns, Uri.CheckHostName(hostname));
            var template = properties.GetProperty("template");
            Assert.Equal(0, template.GetProperty("scale").GetProperty("minReplicas").GetInt32());
            var container = Assert.Single(template.GetProperty("containers").EnumerateArray());
            var name = container.GetProperty("name").GetString()!;
            Assert.True(name is "api" or "frontend", "Only the two requested HTTP apps should be deployed.");
            urls.Add(name, new Uri($"https://{hostname}"));

            var registry = Assert.Single(configuration.GetProperty("registries").EnumerateArray());
            var server = registry.GetProperty("server").GetString()!;
            Assert.EndsWith(".azurecr.io", server, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith($"{server}/", container.GetProperty("image").GetString(), StringComparison.OrdinalIgnoreCase);
            var identity = registry.GetProperty("identity").GetString()!;
            Assert.Contains("/providers/Microsoft.ManagedIdentity/userAssignedIdentities/", identity, StringComparison.OrdinalIgnoreCase);
            // Azure can return "resourcegroups" here but "resourceGroups" in the registry binding.
            Assert.Contains(app.GetProperty("identity").GetProperty("userAssignedIdentities").EnumerateObject(),
                assignedIdentity => string.Equals(assignedIdentity.Name, identity, StringComparison.OrdinalIgnoreCase));
            Assert.True(!registry.TryGetProperty("passwordSecretRef", out var password) || string.IsNullOrEmpty(password.GetString()),
                "Image pulls must not fall back to registry password authentication.");

            var secretEnvironment = Assert.Single(container.GetProperty("env").EnumerateArray(),
                entry => entry.GetProperty("name").GetString() == "MANUAL_SECRET");
            Assert.True(!secretEnvironment.TryGetProperty("value", out var literal) || literal.ValueKind == JsonValueKind.Null,
                "The manual secret must be a secret reference, not a literal environment value.");
            var secretName = secretEnvironment.GetProperty("secretRef").GetString();
            Assert.False(string.IsNullOrEmpty(secretName));
            var secret = Assert.Single(configuration.GetProperty("secrets").EnumerateArray(),
                entry => entry.GetProperty("name").GetString() == secretName);
            Assert.True(!secret.TryGetProperty("keyVaultUrl", out var keyVaultUrl) || string.IsNullOrEmpty(keyVaultUrl.GetString()),
                "The test must use a manual secret, not a Key Vault reference.");
        }

        var frontend = Assert.Single(apps, app =>
            app.GetProperty("properties").GetProperty("template").GetProperty("containers")[0].GetProperty("name").GetString() == "frontend");
        var reference = Assert.Single(frontend.GetProperty("properties").GetProperty("template").GetProperty("containers")[0].GetProperty("env").EnumerateArray(),
            entry => entry.GetProperty("name").GetString() == "services__api__http__0");
        Assert.Equal(urls["api"].GetLeftPart(UriPartial.Authority), reference.GetProperty("value").GetString());
        return urls;
    }

    private static async Task<JsonDocument> GetAzureResourceAsync(
        HttpClient client, TokenCredential credential, string url, CancellationToken cancellationToken)
    {
        // Refresh for each query: two full deployments can outlive the first access token.
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static void VerifyDeploymentSummary(string projectDirectory, Dictionary<string, Uri> urls)
    {
        var summary = File.ReadAllText(Path.Combine(projectDirectory, DeploymentLogFile));
        foreach (var uri in urls.Values)
        {
            // Check only public URLs; never include the complete deployment log in an assertion.
            Assert.True(summary.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal),
                "The deployment summary must include the provider-returned public HTTPS URL.");
        }
    }

    private static async Task VerifyPublicCallAsync(Dictionary<string, Uri> urls, CancellationToken cancellationToken)
    {
        // Scale-to-zero is verified in the provider configuration; this probe verifies requests
        // succeed with that configuration, not idle timing.
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readiness.CancelAfter(TimeSpan.FromMinutes(5));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(75) };

        while (true)
        {
            readiness.Token.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(urls["frontend"], readiness.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>(readiness.Token);
                    if (result.GetProperty("message").GetString() == ApiMessage)
                    {
                        Assert.Equal(urls["api"].GetLeftPart(UriPartial.Authority), result.GetProperty("backend").GetString());

                        // The frontend call above proves the correct secret is accepted. Probe both
                        // an absent and a deliberately wrong header so a comparison that accidentally
                        // accepts any value cannot pass.
                        using var unauthenticated = await client.GetAsync(urls["api"], readiness.Token);
                        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

                        using var wrongSecretRequest = new HttpRequestMessage(HttpMethod.Get, urls["api"]);
                        wrongSecretRequest.Headers.Add("X-Express-Test-Secret", "wrong-express-test-secret");
                        using var wrongSecret = await client.SendAsync(wrongSecretRequest, readiness.Token);
                        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // DNS/connection readiness can lag a successful Azure deployment.
            }
            catch (OperationCanceledException) when (!readiness.IsCancellationRequested)
            {
                // A per-request cold-start timeout can be retried within the readiness budget.
            }

            await Task.Delay(TimeSpan.FromSeconds(10), readiness.Token);
        }
    }

    private static async Task CleanupResourceGroupAsync(string subscriptionId, string resourceGroupName)
    {
        // Cleanup must still run after test cancellation. Await deletion and verify absence rather
        // than reporting success merely because a fire-and-forget process was launched.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        string[] existsArguments = ["group", "exists", "--subscription", subscriptionId, "--name", resourceGroupName];
        if ((await RunAzureCliAsync(existsArguments, timeout.Token)).Trim() == "false")
        {
            return;
        }

        await RunAzureCliAsync(
            ["group", "delete", "--subscription", subscriptionId, "--name", resourceGroupName, "--yes"],
            timeout.Token);
        Assert.Equal("false", (await RunAzureCliAsync(existsArguments, timeout.Token)).Trim());
    }

    private static async Task<string> RunAzureCliAsync(string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("az")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        // Read both pipes concurrently to avoid deadlock. Keep draining after command cancellation;
        // the finally block kills the process and independently bounds shutdown and pipe draining.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var result = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Azure resource group command failed ({process.ExitCode}): {error}");
            }

            return result;
        }
        finally
        {
            // Shutdown must outlive command cancellation, but must not block Azure cleanup indefinitely.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            }

            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }
}
