// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Azure.Core;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AzureEntraIdDeploymentTests(ITestOutputHelper output)
{
    private const string RedirectUri = "https://example.invalid/signin-oidc";
    private const string UpdatedRedirectUri = "https://example.invalid/updated-signin-oidc";
    private const string ParameterRedirectUris = "https://example.invalid/signout-oidc,https://example.invalid/callback";
    private const string UpdatedParameterRedirectUris = "https://example.invalid/updated-callback";
    private const string EndpointCallbackPath = "/endpoint-callback";

    [Fact]
    public async Task DeployEntraRegistrationAndRedeployWithStableIds()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Deployment terminal automation requires Linux.");

        var subscriptionId = DotnetProjectDeploymentHelpers.GetSubscriptionId();
        Assert.True(Guid.TryParse(subscriptionId, out _), "The deployment subscription must be a GUID.");
        Assert.True(Guid.TryParse(AzureAuthenticationHelpers.GetTenantId(), out var tenantId),
            "Set AZURE_TENANT_ID to the deployment tenant ID.");
        var strategy = DeploymentE2ETestHelpers.GetCurrentBuildCliInstallStrategy();
        Assert.SkipUnless(
            strategy.Mode is CliInstallMode.Preinstalled or CliInstallMode.LocalHive or CliInstallMode.LocalArchive or CliInstallMode.PullRequest,
            "This scenario requires current-build artifacts. Set ASPIRE_E2E_ARCHIVE for a local run.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(60));
        var cancellationToken = timeout.Token;
        using var workspace = TemporaryWorkspace.Create(output);
        using var client = new HttpClient();
        var credential = AzureAuthenticationHelpers.GetAzureCredential();
        var suffix = Guid.NewGuid().ToString("N");
        var registrationName = $"identity-{suffix}";
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName($"entra-{suffix[..8]}");
        var resourceGroupUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}?api-version=2021-04-01";
        // Address the same immutable alternate key as Graph Bicep upserts, not the non-unique display name.
        // https://learn.microsoft.com/graph/api/application-upsert
        var applicationUrl = $"https://graph.microsoft.com/v1.0/applications(uniqueName='{registrationName}')";
        var started = DateTime.UtcNow;
        var deploymentAttempted = false;
        Exception? deploymentFailure = null;
        var deploymentUrls = new Dictionary<string, string>();

        // Only take cleanup ownership after proving these test-specific names are unused.
        using (var existingGroup = await ReadResourceAsync(client, credential, resourceGroupUrl, cancellationToken))
        using (var existingApplication = await ReadResourceAsync(client, credential, applicationUrl, cancellationToken))
        {
            Assert.Null(existingGroup);
            Assert.Null(existingApplication);
        }

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
                await auto.AspireNewAsync("EntraDeployment", counter, template: AspireTemplate.EmptyAppHost);
                await auto.RunCommandAsync("cd EntraDeployment", counter);
                await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.AppContainers");
                await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.EntraId");
                await auto.RunCommandAsync("dotnet new web -n Consumer --no-restore", counter, TimeSpan.FromMinutes(2));
                var projectDirectory = Path.Combine(workspace.Path, "EntraDeployment");
                WriteApplication(projectDirectory);

                await auto.RunCommandAsync(
                    "unset ASPIRE_PLAYGROUND Azure__Location Azure__ResourceGroup Azure__SubscriptionId Azure__TenantId Azure__TenantResourceSuffix && " +
                    $"export AZURE__LOCATION=westus3 AZURE__RESOURCEGROUP={AspireCliShellCommandHelpers.QuoteBashArg(resourceGroupName)} " +
                    $"AZURE__SUBSCRIPTIONID={AspireCliShellCommandHelpers.QuoteBashArg(subscriptionId)} " +
                    $"AZURE__TENANTID={tenantId:D} AZURE__TENANTRESOURCESUFFIX={suffix}",
                    counter);

                await auto.RunCommandAsync(
                    $"export Parameters__redirecturis={AspireCliShellCommandHelpers.QuoteBashArg(ParameterRedirectUris)}", counter);

                deploymentAttempted = true;
                await DeployAsync(auto, counter);
                var firstIds = await VerifyDeploymentAsync(
                    client, credential, subscriptionId, resourceGroupName, registrationName, tenantId, [RedirectUri, .. ParameterRedirectUris.Split(',')], deploymentUrls, cancellationToken);

                // Change actual Graph settings, including the deployment-supplied parameter, so the second deployment
                // must update the registration rather than only demonstrating that a cached deployment preserves its IDs.
                await auto.RunCommandAsync(
                    $"export Parameters__redirecturis={AspireCliShellCommandHelpers.QuoteBashArg(UpdatedParameterRedirectUris)}", counter);
                DotnetProjectDeploymentHelpers.ReplaceInFile(
                    Path.Combine(projectDirectory, "apphost.cs"), RedirectUri, UpdatedRedirectUri);
                await DeployAsync(auto, counter);
                var secondIds = await VerifyDeploymentAsync(
                    client, credential, subscriptionId, resourceGroupName, registrationName, tenantId, [UpdatedRedirectUri, .. UpdatedParameterRedirectUris.Split(',')], deploymentUrls, cancellationToken);
                Assert.Equal(firstIds, secondIds);

                await auto.TypeAsync("exit");
                await auto.EnterAsync();
                await pendingRun.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            finally
            {
                // Stop terminal-driven deployment before cleanup, even after the test budget expires.
                await terminalCancellation.CancelAsync();
                try
                {
                    await pendingRun.WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (OperationCanceledException) when (terminalCancellation.IsCancellationRequested)
                {
                    // Terminal cancellation is expected on failure or timeout.
                }
            }
        }
        catch (Exception ex)
        {
            deploymentFailure = ex;
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployEntraRegistrationAndRedeployWithStableIds), resourceGroupName, ex.Message);
            throw;
        }
        finally
        {
            if (deploymentAttempted)
            {
                // Directory objects are not owned by the resource group. Both cleanup paths must run,
                // including when deployment failed before outputs could be read.
                try
                {
                    await CleanupAsync(client, credential, resourceGroupUrl, applicationUrl);
                    DeploymentReporter.ReportCleanupStatus(resourceGroupName, true, "Resource group and Entra directory objects deleted.");
                }
                catch (Exception ex)
                {
                    DeploymentReporter.ReportCleanupStatus(resourceGroupName, false, ex.Message);
                    var failures = new List<Exception> { ex };
                    if (deploymentFailure is not null)
                    {
                        failures.Insert(0, deploymentFailure);
                    }

                    throw new AggregateException("Entra deployment cleanup failed.", failures);
                }
            }
        }

        DeploymentReporter.ReportDeploymentSuccess(
            nameof(DeployEntraRegistrationAndRedeployWithStableIds), resourceGroupName, deploymentUrls, DateTime.UtcNow - started);
    }

    private static Task DeployAsync(Hex1bTerminalAutomator auto, SequenceCounter counter)
    {
        // Graph can reject service-principal creation immediately after creating its backing app
        // in the same tenant. Retry only this observed consistency failure, preserving other errors.
        // https://github.com/microsoftgraph/msgraph-bicep-types/issues/275
        // The deployment error contains: "Authorization_RequestDenied: When using this permission,
        // the backing application of the service principal being created must in the local tenant".
        const string command =
            "( log=$(mktemp) || exit; trap 'rm -f \"$log\"' EXIT; " +
            "for attempt in 1 2 3; do " +
            "aspire deploy --clear-cache --non-interactive 2>&1 | tee \"$log\"; status=${PIPESTATUS[0]}; " +
            "if (( status == 0 )); then exit 0; fi; " +
            "if (( attempt == 3 )) || ! grep -Fq 'the backing application of the service principal being created must in the' \"$log\"; " +
            "then exit \"$status\"; fi; " +
            "echo 'Retrying deployment after Graph could not resolve the newly created backing application in its tenant (30s).'; " +
            "sleep 30; done )";

        // Match this command's exit prompt, not a previous deployment's success banner.
        return auto.RunCommandAsync(command, counter, TimeSpan.FromMinutes(25));
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
            var identity = builder.AddEntraIdApplication("identity")
                .WithRedirectUris("{{RedirectUri}}")
                .WithRedirectUris(builder.AddParameter("redirecturis"));

            var consumer = builder.AddProject("consumer", "Consumer/Consumer.csproj")
                .WithHttpEndpoint(targetPort: 8080)
                .WithExternalHttpEndpoints()
                .WithReference(identity);

            // Derived from the consumer's deployed host name, which is only known from the environment outputs.
            identity.WithRedirectUris(consumer.GetEndpoint("http"), "{{EndpointCallbackPath}}");

            builder.Build().Run();
            """);
        File.Delete(Path.Combine(projectDirectory, "Consumer", "Properties", "launchSettings.json"));
        File.WriteAllText(Path.Combine(projectDirectory, "Consumer", "Program.cs"), """
            var builder = WebApplication.CreateBuilder(args);
            var tenantId = builder.Configuration["AzureAd:TenantId"]
                ?? throw new InvalidOperationException("Entra tenant ID is missing.");
            var clientId = builder.Configuration["AzureAd:ClientId"]
                ?? throw new InvalidOperationException("Entra client ID is missing.");
            var app = builder.Build();
            app.MapGet("/", () => $"{tenantId}/{clientId}");
            app.Run();
            """);
    }

    private static async Task<(Guid ObjectId, Guid ClientId, Guid ServicePrincipalId)> VerifyDeploymentAsync(
        HttpClient client, TokenCredential credential, string subscriptionId, string resourceGroupName,
        string registrationName, Guid tenantId, string[] redirectUris, Dictionary<string, string> deploymentUrls,
        CancellationToken cancellationToken)
    {
        var application = await WaitForResourceAsync(client, credential,
            $"https://graph.microsoft.com/v1.0/applications(uniqueName='{registrationName}')",
            resource =>
            {
                // Graph can reorder redirect URIs. The endpoint-derived URI's host name is verified below.
                var registered = resource.GetProperty("web").GetProperty("redirectUris").EnumerateArray()
                    .Select(uri => uri.GetString()).ToArray();
                return registered.Length == redirectUris.Length + 1
                    && redirectUris.All(uri => registered.Contains(uri, StringComparer.Ordinal))
                    && registered.Any(uri => uri is not null && uri.StartsWith("https://", StringComparison.Ordinal) && uri.EndsWith(EndpointCallbackPath, StringComparison.Ordinal));
            },
            cancellationToken);
        Assert.Equal(registrationName, application.GetProperty("displayName").GetString());
        Assert.Equal(registrationName, application.GetProperty("uniqueName").GetString());
        Assert.Equal("AzureADMyOrg", application.GetProperty("signInAudience").GetString());
        var clientId = application.GetProperty("appId").GetGuid();
        var principal = await WaitForResourceAsync(client, credential,
            $"https://graph.microsoft.com/v1.0/servicePrincipals(appId='{clientId:D}')",
            resource => resource.GetProperty("appId").GetGuid() == clientId
                && resource.GetProperty("tags").EnumerateArray()
                    .Any(tag => tag.GetString() == "WindowsAzureActiveDirectoryIntegratedApp"),
            cancellationToken);

        using var consumer = await ReadResourceAsync(client, credential,
            $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.App/containerApps/consumer?api-version=2026-03-02-preview",
            cancellationToken);
        Assert.NotNull(consumer);
        var properties = consumer.RootElement.GetProperty("properties");
        var container = Assert.Single(properties.GetProperty("template").GetProperty("containers").EnumerateArray());
        var environment = container.GetProperty("env").EnumerateArray().ToArray();
        Assert.Equal(tenantId.ToString("D"), Assert.Single(environment,
            entry => entry.GetProperty("name").GetString() == "AzureAd__TenantId").GetProperty("value").GetString());
        Assert.Equal(clientId.ToString("D"), Assert.Single(environment,
            entry => entry.GetProperty("name").GetString() == "AzureAd__ClientId").GetProperty("value").GetString());
        var ingress = properties.GetProperty("configuration").GetProperty("ingress");
        Assert.True(ingress.GetProperty("external").GetBoolean());
        Assert.False(ingress.GetProperty("allowInsecure").GetBoolean());
        var hostname = ingress.GetProperty("fqdn").GetString();
        Assert.Equal(UriHostNameType.Dns, Uri.CheckHostName(hostname));
        var registeredUris = application.GetProperty("web").GetProperty("redirectUris").EnumerateArray().Select(uri => uri.GetString()).ToArray();
        Assert.Equal(
            redirectUris.Append($"https://{hostname}{EndpointCallbackPath}").Order(StringComparer.Ordinal),
            registeredUris.Order(StringComparer.Ordinal));
        var url = $"https://{hostname}";
        await DotnetProjectDeploymentHelpers.VerifyResponseAsync(url, $"{tenantId:D}/{clientId:D}", cancellationToken);
        deploymentUrls["Consumer"] = url;
        return (application.GetProperty("id").GetGuid(), clientId, principal.GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> WaitForResourceAsync(
        HttpClient client, TokenCredential credential, string url, Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readiness.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            using var resource = await ReadResourceAsync(client, credential, url, readiness.Token);
            if (resource is not null && predicate(resource.RootElement))
            {
                return resource.RootElement.Clone();
            }

            // Graph reads can lag extension completion; poll the actual provider state.
            await Task.Delay(TimeSpan.FromSeconds(2), readiness.Token);
        }
    }

    private static async Task CleanupDirectoryAsync(HttpClient client, TokenCredential credential, string applicationUrl)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var application = await ReadResourceAsync(client, credential, applicationUrl, cleanup.Token);
        if (application is null)
        {
            return;
        }

        var clientId = application.RootElement.GetProperty("appId").GetGuid();
        var objectId = application.RootElement.GetProperty("id").GetGuid();
        var principalUrl = $"https://graph.microsoft.com/v1.0/servicePrincipals(appId='{clientId:D}')";
        // Application deletion cascades to its service principal. Deleting the principal first
        // can make the cascade fail with DirectoryObjectNotFound while replicas still reference it.
        // Verify both objects disappear rather than issuing competing deletes.
        // https://learn.microsoft.com/graph/api/application-delete
        await DeleteResourceAsync(client, credential, $"https://graph.microsoft.com/v1.0/applications/{objectId:D}", cleanup.Token);
        await Task.WhenAll(
            WaitForDeletionAsync(client, credential, applicationUrl, cleanup.Token),
            WaitForDeletionAsync(client, credential, principalUrl, cleanup.Token));
    }

    private static async Task CleanupAsync(
        HttpClient client, TokenCredential credential, string resourceGroupUrl, string applicationUrl)
    {
        var failures = new List<Exception>();
        try
        {
            // Finish ARM teardown before Graph cleanup to stop pending deployments from creating
            // directory objects while they are being cleaned up.
            await CleanupResourceGroupAsync(client, credential, resourceGroupUrl);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            await CleanupDirectoryAsync(client, credential, applicationUrl);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Azure resource group or Entra directory cleanup failed.", failures);
        }
    }

    private static async Task CleanupResourceGroupAsync(HttpClient client, TokenCredential credential, string resourceGroupUrl)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        await DeleteResourceAsync(client, credential, resourceGroupUrl, cleanup.Token);
        await WaitForDeletionAsync(client, credential, resourceGroupUrl, cleanup.Token);
    }

    private static async Task WaitForDeletionAsync(
        HttpClient client, TokenCredential credential, string url, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var resource = await ReadResourceAsync(client, credential, url, cancellationToken);
            if (resource is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private static async Task DeleteResourceAsync(HttpClient client, TokenCredential credential, string url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, credential, HttpMethod.Delete, url, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private static async Task<JsonDocument?> ReadResourceAsync(
        HttpClient client, TokenCredential credential, string url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, credential, HttpMethod.Get, url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, TokenCredential credential, HttpMethod method, string url, CancellationToken cancellationToken)
    {
        var scope = new Uri(url).Host switch
        {
            "graph.microsoft.com" => "https://graph.microsoft.com/.default",
            "management.azure.com" => "https://management.azure.com/.default",
            _ => throw new InvalidOperationException("Unexpected deployment test endpoint.")
        };
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await client.SendAsync(request, cancellationToken);
    }
}
