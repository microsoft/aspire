// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests.Helpers;

/// <summary>
/// The state of one Entra deployment scenario run by <see cref="EntraDeploymentHelpers.RunAsync"/>.
/// </summary>
internal sealed class EntraDeploymentContext
{
    public required ITestOutputHelper Output { get; init; }
    public required CliInstallStrategy Strategy { get; init; }
    public required TemporaryWorkspace Workspace { get; init; }
    public required HttpClient Client { get; init; }
    public required TokenCredential Credential { get; init; }
    public required string SubscriptionId { get; init; }
    public required Guid TenantId { get; init; }
    public required string Suffix { get; init; }
    public required string ResourceGroupName { get; init; }

    /// <summary>
    /// The unique names of the Entra registrations the scenario deploys, which Graph Bicep upserts by.
    /// </summary>
    public required string[] RegistrationNames { get; init; }

    public Dictionary<string, string> DeploymentUrls { get; } = [];

    /// <summary>
    /// Set by the scenario just before it starts deploying. Cleanup only runs once a deployment may have created resources.
    /// </summary>
    public bool DeploymentAttempted { get; set; }

    public string ResourceGroupUrl =>
        $"https://management.azure.com/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroupName}?api-version=2021-04-01";

    /// <summary>
    /// Points the deployment at the test subscription, tenant and resource group. The tenant resource suffix is appended to each
    /// registration's name so that they are unique to this run.
    /// </summary>
    public Task ConfigureAzureEnvironmentAsync(Hex1bTerminalAutomator auto, SequenceCounter counter)
    {
        return auto.RunCommandAsync(
            "unset ASPIRE_PLAYGROUND Azure__Location Azure__ResourceGroup Azure__SubscriptionId Azure__TenantId Azure__TenantResourceSuffix && " +
            $"export AZURE__LOCATION=westus3 AZURE__RESOURCEGROUP={AspireCliShellCommandHelpers.QuoteBashArg(ResourceGroupName)} " +
            $"AZURE__SUBSCRIPTIONID={AspireCliShellCommandHelpers.QuoteBashArg(SubscriptionId)} " +
            $"AZURE__TENANTID={TenantId:D} AZURE__TENANTRESOURCESUFFIX={Suffix}",
            counter);
    }
}

/// <summary>
/// Shared harness and Microsoft Graph/Azure Resource Manager helpers for the Entra deployment tests.
/// </summary>
internal static class EntraDeploymentHelpers
{
    /// <summary>
    /// Runs an Entra deployment scenario: validates the prerequisites, refuses to reuse existing objects, drives the scenario
    /// in a terminal, and always deletes the resource group and the Entra registrations afterwards.
    /// </summary>
    /// <param name="output">The test output.</param>
    /// <param name="testName">The name used in the deployment report.</param>
    /// <param name="resourceGroupLabel">The test-specific part of the resource group name.</param>
    /// <param name="getRegistrationNames">Gets the unique names of the registrations for a run's unique suffix.</param>
    /// <param name="scenario">Creates the application, deploys it and verifies the result.</param>
    internal static async Task RunAsync(
        ITestOutputHelper output,
        string testName,
        string resourceGroupLabel,
        Func<string, string[]> getRegistrationNames,
        Func<EntraDeploymentContext, Hex1bTerminalAutomator, SequenceCounter, CancellationToken, Task> scenario)
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
        var deployment = new EntraDeploymentContext
        {
            Output = output,
            Strategy = strategy,
            Workspace = workspace,
            Client = client,
            Credential = credential,
            SubscriptionId = subscriptionId,
            TenantId = tenantId,
            Suffix = suffix,
            ResourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName($"{resourceGroupLabel}-{suffix[..8]}"),
            RegistrationNames = getRegistrationNames(suffix)
        };
        var applicationUrls = deployment.RegistrationNames.Select(GetApplicationUrl).ToArray();
        var started = DateTime.UtcNow;
        Exception? deploymentFailure = null;

        // Only take cleanup ownership after proving these test-specific names are unused.
        using (var existingGroup = await ReadResourceAsync(client, credential, deployment.ResourceGroupUrl, cancellationToken))
        {
            Assert.Null(existingGroup);
        }

        foreach (var applicationUrl in applicationUrls)
        {
            using var existingApplication = await ReadResourceAsync(client, credential, applicationUrl, cancellationToken);
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
                await scenario(deployment, auto, counter, cancellationToken);

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
            DeploymentReporter.ReportDeploymentFailure(testName, deployment.ResourceGroupName, ex.Message);
            throw;
        }
        finally
        {
            if (deployment.DeploymentAttempted)
            {
                // Directory objects are not owned by the resource group. Both cleanup paths must run,
                // including when deployment failed before outputs could be read.
                try
                {
                    await CleanupAsync(client, credential, deployment.ResourceGroupUrl, applicationUrls, waitForResourceGroup: deploymentFailure is not null);
                    DeploymentReporter.ReportCleanupStatus(
                        deployment.ResourceGroupName,
                        true,
                        deploymentFailure is null
                            ? "Resource group deletion requested and Entra directory objects deleted."
                            : "Resource group and Entra directory objects deleted.");
                }
                catch (Exception ex)
                {
                    DeploymentReporter.ReportCleanupStatus(deployment.ResourceGroupName, false, ex.Message);
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
            testName, deployment.ResourceGroupName, deployment.DeploymentUrls, DateTime.UtcNow - started);
    }

    // Address the same immutable alternate key as Graph Bicep upserts, not the non-unique display name.
    // https://learn.microsoft.com/graph/api/application-upsert
    internal static string GetApplicationUrl(string uniqueName) =>
        $"https://graph.microsoft.com/v1.0/applications(uniqueName='{uniqueName}')";

    internal static Task DeployAsync(Hex1bTerminalAutomator auto, SequenceCounter counter)
    {
        // Graph can reject a request that refers to an object it created moments earlier in the same tenant,
        // because the new object has not replicated yet. Retry only these observed consistency failures, preserving other errors.
        // https://github.com/microsoftgraph/msgraph-bicep-types/issues/275
        // Creating a service principal fails with: "Authorization_RequestDenied: When using this permission,
        // the backing application of the service principal being created must in the local tenant".
        // Creating a federated credential, which addresses its application by unique name, fails with:
        // "Request_ResourceNotFound: Resource '{uniqueName}' does not exist or one of its queried reference-property objects are not present".
        // Setting the default application ID URI, api://{clientId}, on the application that was just created fails with:
        // "InvalidIdentifierUri: The application identifier uri 'api://{clientId}' is invalid".
        // A deployment upserts, so the retry finds the application and carries on.
        const string command =
            "( log=$(mktemp) || exit; trap 'rm -f \"$log\"' EXIT; " +
            "for attempt in 1 2 3; do " +
            "aspire deploy --clear-cache --non-interactive 2>&1 | tee \"$log\"; status=${PIPESTATUS[0]}; " +
            "if (( status == 0 )); then exit 0; fi; " +
            "if (( attempt == 3 )) || ! grep -Fq -e 'the backing application of the service principal being created must in the' -e 'Request_ResourceNotFound' -e 'InvalidIdentifierUri' \"$log\"; " +
            "then exit \"$status\"; fi; " +
            "echo 'Retrying deployment after Graph could not resolve a newly created object in its tenant (30s).'; " +
            "sleep 30; done )";

        // Match this command's exit prompt, not a previous deployment's success banner.
        return auto.RunCommandAsync(command, counter, TimeSpan.FromMinutes(25));
    }

    internal static async Task<JsonElement> WaitForResourceAsync(
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

    private static async Task CleanupDirectoryAsync(HttpClient client, TokenCredential credential, string[] applicationUrls)
    {
        // These objects outlive the resource group, so a failure to delete one must not leave the others behind.
        var failures = new List<Exception>();
        foreach (var applicationUrl in applicationUrls)
        {
            try
            {
                await CleanupApplicationAsync(client, credential, applicationUrl);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Entra directory cleanup failed.", failures);
        }
    }

    private static async Task CleanupApplicationAsync(HttpClient client, TokenCredential credential, string applicationUrl)
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
        HttpClient client, TokenCredential credential, string resourceGroupUrl, string[] applicationUrls, bool waitForResourceGroup)
    {
        var failures = new List<Exception>();
        try
        {
            await CleanupResourceGroupAsync(client, credential, resourceGroupUrl, waitForResourceGroup);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            await CleanupDirectoryAsync(client, credential, applicationUrls);
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

    private static async Task CleanupResourceGroupAsync(
        HttpClient client, TokenCredential credential, string resourceGroupUrl, bool waitForDeletion)
    {
        // Deleting a Container Apps environment can take well over 15 minutes.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        await DeleteResourceAsync(client, credential, resourceGroupUrl, cleanup.Token);

        // After a failure or timeout, a deployment can still be running on the Azure side. Finish ARM teardown before the directory
        // cleanup, so that the deployment can't create directory objects while they are being deleted. After a deployment that completed,
        // nothing can create them, so, like the other deployment tests, don't wait for the group to disappear.
        if (waitForDeletion)
        {
            await WaitForDeletionAsync(client, credential, resourceGroupUrl, cleanup.Token);
        }
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

    internal static async Task<JsonDocument?> ReadResourceAsync(
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
