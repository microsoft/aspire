// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Azure.Core;
using Xunit;
using static Aspire.Deployment.EndToEnd.Tests.Helpers.EntraDeploymentHelpers;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AzureEntraIdStarterDeploymentTests(ITestOutputHelper output)
{
    /// <summary>
    /// Deploys the Aspire starter application with Entra added: a protected API and a web front end that declares permission to the
    /// API's delegated scope. The scope is declared, not granted: the scenario adds neither admin consent nor pre-authorization. The
    /// sign-in flow can't be automated, so this verifies the registrations, the declared permission between them and the configuration
    /// that each deployed app receives. The publish snapshot test in Aspire.Hosting.Azure.EntraId.Tests covers the same AppHost.
    /// </summary>
    [Fact]
    public Task DeployStarterWithEntraApiAndWebRegistrations() =>
        RunAsync(
            output,
            nameof(DeployStarterWithEntraApiAndWebRegistrations),
            "entra-starter",
            // The tenant resource suffix is appended to each registration's name, which is the uniqueName that Graph Bicep upserts by.
            suffix => [$"apiservice-entra-{suffix}", $"webfrontend-entra-{suffix}"],
            async (deployment, auto, counter, cancellationToken) =>
            {
                const string projectName = "EntraStarter";
                await auto.PrepareEnvironmentAsync(deployment.Workspace, counter);
                await auto.InstallAspireCliAsync(deployment.Strategy, counter, output);
                await auto.AspireNewAsync(projectName, counter, useRedisCache: false);
                await auto.RunCommandAsync($"cd {projectName}", counter);
                await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.AppContainers");
                await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.EntraId");
                WriteStarterWithEntra(Path.Combine(deployment.Workspace.Path, projectName), projectName);
                await auto.RunCommandAsync($"cd {projectName}.AppHost", counter);
                await deployment.ConfigureAzureEnvironmentAsync(auto, counter);

                deployment.DeploymentAttempted = true;
                await DeployAsync(auto, counter);
                await VerifyStarterDeploymentAsync(deployment, cancellationToken);
            });

    private static void WriteStarterWithEntra(string solutionDirectory, string projectName)
    {
        var appHostPath = Path.Combine(solutionDirectory, $"{projectName}.AppHost", "AppHost.cs");
        File.WriteAllText(appHostPath, $$"""
            var builder = DistributedApplication.CreateBuilder(args);
            builder.AddAzureContainerAppEnvironment("env");

            var apiEntra = builder.AddEntraIdApplication("apiservice-entra")
                .WithScope("access_as_user", "Access the weather API on behalf of the signed-in user");

            var apiService = builder.AddDotnetProject("apiservice", "../{{projectName}}.ApiService/{{projectName}}.ApiService.csproj")
                .WithReference(apiEntra)
                .WithHttpHealthCheck("/health");

            var webEntra = builder.AddEntraIdApplication("webfrontend-entra")
                .WithApiPermissions(apiEntra, "access_as_user")
                .PublishWithFederatedManagedIdentityCredential();

            var webFrontend = builder.AddDotnetProject("webfrontend", "../{{projectName}}.Web/{{projectName}}.Web.csproj")
                .WithExternalHttpEndpoints()
                .WithHttpHealthCheck("/health")
                .WithReference(webEntra, connectionName: null, downstreamApisPrefix: "MyDownstreamApis")
                .WithReference(apiService)
                .WaitFor(apiService);

            webEntra.WithRedirectUris(webFrontend.GetEndpoint("https"), "/signin-oidc", "/signout-callback-oidc");

            builder.Build().Run();
            """);

        // Neither app signs anyone in. Each reports the Entra settings that reached its configuration so the test can
        // prove the environment variables of the container app are read by the running application.
        DotnetProjectDeploymentHelpers.ReplaceInFile(Path.Combine(solutionDirectory, $"{projectName}.ApiService", "Program.cs"),
            "app.Run();", """
            app.MapGet("/entra-config", (IConfiguration configuration) =>
                $"{configuration["AzureAd:ClientId"]}|{configuration["AzureAd:Scopes"]}");
            app.Run();
            """);
        DotnetProjectDeploymentHelpers.ReplaceInFile(Path.Combine(solutionDirectory, $"{projectName}.Web", "Program.cs"),
            "app.Run();", """
            app.MapGet("/entra-config", async (IConfiguration configuration, IHttpClientFactory clients, CancellationToken cancellationToken) =>
                $"{configuration["AzureAd:ClientId"]}|{configuration["MyDownstreamApis:apiservice:Scopes:0"]}|" +
                await clients.CreateClient().GetStringAsync("https+http://apiservice/entra-config", cancellationToken));
            app.Run();
            """);
    }

    private static async Task VerifyStarterDeploymentAsync(EntraDeploymentContext deployment, CancellationToken cancellationToken)
    {
        var client = deployment.Client;
        var credential = deployment.Credential;
        var tenantId = deployment.TenantId;
        var apiUniqueName = deployment.RegistrationNames[0];
        var webUniqueName = deployment.RegistrationNames[1];

        var apiApplication = await WaitForResourceAsync(client, credential,
            GetApplicationUrl(apiUniqueName),
            resource => resource.GetProperty("identifierUris").GetArrayLength() == 1
                && resource.GetProperty("api").GetProperty("oauth2PermissionScopes").GetArrayLength() == 1,
            cancellationToken);
        var apiClientId = apiApplication.GetProperty("appId").GetGuid();
        // The Application ID URI defaults to api://{clientId}, which can only be set once Graph has assigned the client ID.
        Assert.Equal($"api://{apiClientId:D}", Assert.Single(apiApplication.GetProperty("identifierUris").EnumerateArray()).GetString());
        var scope = Assert.Single(apiApplication.GetProperty("api").GetProperty("oauth2PermissionScopes").EnumerateArray());
        Assert.Equal("access_as_user", scope.GetProperty("value").GetString());
        Assert.Equal("User", scope.GetProperty("type").GetString());
        Assert.True(scope.GetProperty("isEnabled").GetBoolean());
        var scopeId = scope.GetProperty("id").GetGuid();
        Assert.Equal(0, apiApplication.GetProperty("api").GetProperty("preAuthorizedApplications").GetArrayLength());

        // The API is deployed to its own container app, whose registration is only an identity: it has no redirect URIs.
        Assert.Equal(0, apiApplication.GetProperty("web").GetProperty("redirectUris").GetArrayLength());

        var webApplication = await WaitForResourceAsync(client, credential,
            GetApplicationUrl(webUniqueName),
            resource => resource.GetProperty("web").GetProperty("redirectUris").GetArrayLength() == 2
                && resource.GetProperty("requiredResourceAccess").GetArrayLength() == 1,
            cancellationToken);
        var webClientId = webApplication.GetProperty("appId").GetGuid();
        var webObjectId = webApplication.GetProperty("id").GetGuid();
        Assert.Equal(webUniqueName, webApplication.GetProperty("displayName").GetString());
        Assert.Equal(0, webApplication.GetProperty("identifierUris").GetArrayLength());

        // Calling the API on the user's behalf requires the delegated scope that the API exposes, identified by the ID Graph stores.
        var required = Assert.Single(webApplication.GetProperty("requiredResourceAccess").EnumerateArray());
        Assert.Equal(apiClientId, required.GetProperty("resourceAppId").GetGuid());
        var access = Assert.Single(required.GetProperty("resourceAccess").EnumerateArray());
        Assert.Equal(scopeId, access.GetProperty("id").GetGuid());
        Assert.Equal("Scope", access.GetProperty("type").GetString());

        foreach (var clientId in new[] { apiClientId, webClientId })
        {
            await WaitForResourceAsync(client, credential,
                $"https://graph.microsoft.com/v1.0/servicePrincipals(appId='{clientId:D}')",
                resource => resource.GetProperty("appId").GetGuid() == clientId
                    && resource.GetProperty("tags").EnumerateArray()
                        .Any(tag => tag.GetString() == "WindowsAzureActiveDirectoryIntegratedApp"),
                cancellationToken);
        }

        var resourceGroupUrl = $"https://management.azure.com/subscriptions/{deployment.SubscriptionId}/resourceGroups/{deployment.ResourceGroupName}";
        using var identities = await ReadResourceAsync(client, credential,
            $"{resourceGroupUrl}/providers/Microsoft.ManagedIdentity/userAssignedIdentities?api-version=2023-01-31", cancellationToken);
        Assert.NotNull(identities);
        var identity = Assert.Single(identities.RootElement.GetProperty("value").EnumerateArray(),
            entry => entry.GetProperty("name").GetString()!.StartsWith("webfrontend_identity", StringComparison.Ordinal));
        var identityClientId = identity.GetProperty("properties").GetProperty("clientId").GetGuid();
        var identityPrincipalId = identity.GetProperty("properties").GetProperty("principalId").GetGuid();

        // The web app authenticates to Entra without a secret: a federated credential trusts the managed identity of its container app.
        using var credentials = await ReadResourceAsync(client, credential,
            $"https://graph.microsoft.com/v1.0/applications/{webObjectId:D}/federatedIdentityCredentials", cancellationToken);
        Assert.NotNull(credentials);
        var federated = Assert.Single(credentials.RootElement.GetProperty("value").EnumerateArray());
        Assert.Equal($"https://login.microsoftonline.com/{tenantId:D}/v2.0", federated.GetProperty("issuer").GetString());
        Assert.Equal(identityPrincipalId.ToString("D"), federated.GetProperty("subject").GetString());
        Assert.Equal("api://AzureADTokenExchange", Assert.Single(federated.GetProperty("audiences").EnumerateArray()).GetString());

        var (webFqdn, webEnvironment) = await GetContainerAppAsync(client, credential, resourceGroupUrl, "webfrontend", cancellationToken);
        Assert.Equal(UriHostNameType.Dns, Uri.CheckHostName(webFqdn));
        Assert.Equal(
            [$"https://{webFqdn}/signin-oidc", $"https://{webFqdn}/signout-callback-oidc"],
            webApplication.GetProperty("web").GetProperty("redirectUris").EnumerateArray().Select(uri => uri.GetString()!).Order(StringComparer.Ordinal));

        var apiScope = $"api://{apiClientId:D}/access_as_user";
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
                ["AzureAd__TenantId"] = tenantId.ToString("D"),
                ["AzureAd__ClientId"] = webClientId.ToString("D"),
                ["AzureAd__ClientCredentials__0__SourceType"] = "SignedAssertionFromManagedIdentity",
                ["AzureAd__ClientCredentials__0__ManagedIdentityClientId"] = identityClientId.ToString("D"),
                ["MyDownstreamApis__apiservice__Scopes__0"] = apiScope
            },
            webEnvironment.Where(entry => entry.Key.StartsWith("AzureAd__", StringComparison.Ordinal) || entry.Key.StartsWith("MyDownstreamApis__", StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value));

        var (_, apiEnvironment) = await GetContainerAppAsync(client, credential, resourceGroupUrl, "apiservice", cancellationToken);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["AzureAd__Instance"] = "https://login.microsoftonline.com/",
                ["AzureAd__TenantId"] = tenantId.ToString("D"),
                ["AzureAd__ClientId"] = apiClientId.ToString("D"),
                ["AzureAd__Scopes"] = "access_as_user"
            },
            apiEnvironment.Where(entry => entry.Key.StartsWith("AzureAd__", StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value));

        // The running web app reads its own settings and the API's, which it reaches through service discovery.
        var url = $"https://{webFqdn}";
        await DotnetProjectDeploymentHelpers.VerifyResponseAsync(
            $"{url}/entra-config", $"{webClientId:D}|{apiScope}|{apiClientId:D}|access_as_user", cancellationToken);
        deployment.DeploymentUrls["webfrontend"] = url;
    }

    private static async Task<(string Fqdn, Dictionary<string, string> Environment)> GetContainerAppAsync(
        HttpClient client, TokenCredential credential, string resourceGroupUrl, string name, CancellationToken cancellationToken)
    {
        using var app = await ReadResourceAsync(client, credential,
            $"{resourceGroupUrl}/providers/Microsoft.App/containerApps/{name}?api-version=2026-03-02-preview", cancellationToken);
        Assert.NotNull(app);
        var properties = app.RootElement.GetProperty("properties");
        var container = Assert.Single(properties.GetProperty("template").GetProperty("containers").EnumerateArray());
        var environment = container.GetProperty("env").EnumerateArray()
            .Where(entry => entry.TryGetProperty("value", out _))
            .ToDictionary(entry => entry.GetProperty("name").GetString()!, entry => entry.GetProperty("value").GetString()!);
        return (properties.GetProperty("configuration").GetProperty("ingress").GetProperty("fqdn").GetString()!, environment);
    }
}
