// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Xunit;
using static Aspire.Deployment.EndToEnd.Tests.Helpers.EntraDeploymentHelpers;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AzureEntraIdAppRoleDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    public Task DeployDirectManagedIdentityRolesAndRedeploy() =>
        RunAsync(
            output,
            nameof(DeployDirectManagedIdentityRolesAndRedeploy),
            "entra-roles",
            suffix => [$"entra-api-{suffix}"],
            async (deployment, auto, counter, cancellationToken) =>
            {
                await auto.PrepareEnvironmentAsync(deployment.Workspace, counter);
                await auto.InstallAspireCliAsync(deployment.Strategy, counter, output);
                await auto.AspireNewAsync("EntraRoles", counter, template: AspireTemplate.EmptyAppHost);
                await auto.RunCommandAsync("cd EntraRoles", counter);
                await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.AppContainers");
                await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.EntraId");
                await auto.RunCommandAsync("dotnet new web -n Api --no-restore && dotnet new web -n Caller --no-restore", counter, TimeSpan.FromMinutes(2));
                await auto.RunCommandAsync("dotnet add Api/Api.csproj package Microsoft.Identity.Web --version 4.16.0 && dotnet add Caller/Caller.csproj package Azure.Identity --version 1.21.0", counter, TimeSpan.FromMinutes(3));
                var projectDirectory = Path.Combine(deployment.Workspace.Path, "EntraRoles");
                WriteApplication(projectDirectory);
                await deployment.ConfigureAzureEnvironmentAsync(auto, counter);

                deployment.DeploymentAttempted = true;
                await DeployAsync(auto, counter);
                var firstAssignments = await VerifyDeploymentAsync(deployment, cancellationToken);

                // Change a Graph property so that a second deployment really reapplies the assignment resources,
                // rather than merely returning cached outputs from a template whose checksum is unchanged.
                DotnetProjectDeploymentHelpers.ReplaceInFile(
                    Path.Combine(projectDirectory, "apphost.cs"), "Read orders.", "Read all orders.");
                await DeployAsync(auto, counter);
                Assert.Equal(firstAssignments, await VerifyDeploymentAsync(deployment, cancellationToken));

                // Incremental Graph provisioning retains omitted assignments. Prove that removing the calls does
                // not silently imply revocation; explicit directory cleanup is required.
                var appHost = Path.Combine(projectDirectory, "apphost.cs");
                DotnetProjectDeploymentHelpers.ReplaceInFile(appHost,
                    "entra.PublishWithAppRoleAssignment(assignedIdentity, \"Orders.Read\");", string.Empty);
                DotnetProjectDeploymentHelpers.ReplaceInFile(appHost,
                    "entra.PublishWithAppRoleAssignment(compute, \"Orders.Read\");", string.Empty);
                await DeployAsync(auto, counter);
                Assert.Equal(firstAssignments, await VerifyDeploymentAsync(deployment, cancellationToken));
            });

    private static void WriteApplication(string projectDirectory)
    {
        var appHostPath = Path.Combine(projectDirectory, "apphost.cs");
        var directives = File.ReadLines(appHostPath).Where(line => line.StartsWith("#:", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(directives);
        File.WriteAllText(appHostPath, $$"""
            {{string.Join(Environment.NewLine, directives)}}

            var builder = DistributedApplication.CreateBuilder(args);
            builder.AddAzureContainerAppEnvironment("env");
            var assignedIdentity = builder.AddAzureUserAssignedIdentity("assigned-identity");
            var unassignedIdentity = builder.AddAzureUserAssignedIdentity("unassigned-identity");
            var computeIdentity = builder.AddAzureUserAssignedIdentity("compute-identity");
            var entra = builder.AddEntraIdApplication("entra-api")
                .WithApplicationIdUri()
                .WithAllowWebApiToBeAuthorizedByACL()
                .WithAppRole(new EntraIdAppRole("Orders.Read", "Read orders", "Read orders.", ["Application"]));
            var api = builder.AddProject("api", "Api/Api.csproj")
                .WithHttpEndpoint(targetPort: 8080)
                .WithReference(entra);
            var assigned = builder.AddProject("assigned", "Caller/Caller.csproj")
                .WithHttpEndpoint(targetPort: 8080)
                .WithExternalHttpEndpoints()
                .WithAzureUserAssignedIdentity(assignedIdentity)
                .WithEnvironment("TargetUrl", api.GetEndpoint("http"))
                .WithEnvironment("TargetClientId", entra.Resource.ClientIdOutputReference)
                .WithEnvironment("ExpectedPrincipalId", assignedIdentity.Resource.PrincipalId)
                .WithEnvironment("ExpectedAccess", "assigned");
            var compute = builder.AddProject("compute", "Caller/Caller.csproj")
                .WithHttpEndpoint(targetPort: 8080)
                .WithExternalHttpEndpoints()
                .WithAzureUserAssignedIdentity(computeIdentity)
                .WithEnvironment("TargetUrl", api.GetEndpoint("http"))
                .WithEnvironment("TargetClientId", entra.Resource.ClientIdOutputReference)
                .WithEnvironment("ExpectedPrincipalId", computeIdentity.Resource.PrincipalId)
                .WithEnvironment("ExpectedAccess", "assigned");
            builder.AddProject("unassigned", "Caller/Caller.csproj")
                .WithHttpEndpoint(targetPort: 8080)
                .WithExternalHttpEndpoints()
                .WithAzureUserAssignedIdentity(unassignedIdentity)
                .WithEnvironment("TargetUrl", api.GetEndpoint("http"))
                .WithEnvironment("TargetClientId", entra.Resource.ClientIdOutputReference)
                .WithEnvironment("ExpectedAccess", "unassigned");
            entra.PublishWithAppRoleAssignment(assignedIdentity, "Orders.Read");
            entra.PublishWithAppRoleAssignment(compute, "Orders.Read");
            builder.Build().Run();
            """);

        File.Delete(Path.Combine(projectDirectory, "Api", "Properties", "launchSettings.json"));
        File.Delete(Path.Combine(projectDirectory, "Caller", "Properties", "launchSettings.json"));
        File.WriteAllText(Path.Combine(projectDirectory, "Api", "Program.cs"), """
            using System.Security.Claims;
            using Microsoft.AspNetCore.Authentication.JwtBearer;
            using Microsoft.Identity.Web;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
            builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters.RoleClaimType = "roles";
            });
            builder.Services.AddAuthorization(options =>
                options.AddPolicy("orders", policy => policy.RequireAuthenticatedUser().RequireRole("Orders.Read")));
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/orders", (ClaimsPrincipal principal) => new
            {
                Audience = principal.FindFirstValue("aud"),
                PrincipalId = principal.FindFirstValue("oid"),
                ClientId = principal.FindFirstValue("azp"),
                Roles = principal.FindAll("roles").Select(claim => claim.Value).ToArray()
            }).RequireAuthorization("orders");
            app.Run();
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "Caller", "Program.cs"), """
            using System.Net;
            using System.Net.Http.Headers;
            using System.Text.Json;
            using Azure.Core;
            using Azure.Identity;

            var builder = WebApplication.CreateBuilder(args);
            var configuration = builder.Configuration;
            var identityClientId = configuration["AZURE_CLIENT_ID"]
                ?? throw new InvalidOperationException("Managed identity client ID is missing.");
            var apiClientId = configuration["TargetClientId"]
                ?? throw new InvalidOperationException("Target API client ID is missing.");
            var targetUrl = configuration["TargetUrl"]
                ?? throw new InvalidOperationException("Target API URL is missing.");
            var assigned = configuration["ExpectedAccess"] == "assigned";
            var credential = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(identityClientId));
            builder.Services.AddHttpClient();
            var app = builder.Build();
            app.MapGet("/", async (IHttpClientFactory clients, CancellationToken cancellationToken) =>
            {
                var client = clients.CreateClient();
                var token = await credential.GetTokenAsync(
                    new TokenRequestContext([$"api://{apiClientId}/.default"]), cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{targetUrl}/orders");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                using var response = await client.SendAsync(request, cancellationToken);
                if (!assigned)
                {
                    if (response.StatusCode != HttpStatusCode.Forbidden)
                    {
                        throw new InvalidOperationException($"Unassigned identity: expected 403, got {(int)response.StatusCode}.");
                    }

                    return "unassigned-denied";
                }

                response.EnsureSuccessStatusCode();
                using var claims = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var payload = claims.RootElement;
                if (payload.GetProperty("audience").GetString() != apiClientId ||
                    payload.GetProperty("principalId").GetString() != configuration["ExpectedPrincipalId"] ||
                    payload.GetProperty("clientId").GetString() != identityClientId ||
                    !payload.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).SequenceEqual(["Orders.Read"]))
                {
                    throw new InvalidOperationException("The API did not validate the expected UAMI audience, object ID, client ID and role.");
                }

                using var anonymous = await client.GetAsync($"{targetUrl}/orders", cancellationToken);
                if (anonymous.StatusCode != HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException($"Anonymous request: expected 401, got {(int)anonymous.StatusCode}.");
                }

                var wrongAudience = await credential.GetTokenAsync(
                    new TokenRequestContext(["https://management.azure.com/.default"]), cancellationToken);
                using var wrongRequest = new HttpRequestMessage(HttpMethod.Get, $"{targetUrl}/orders");
                wrongRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wrongAudience.Token);
                using var wrongResponse = await client.SendAsync(wrongRequest, cancellationToken);
                if (wrongResponse.StatusCode != HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException($"Wrong-audience request: expected 401, got {(int)wrongResponse.StatusCode}.");
                }

                // Return only the verdict, never a bearer token. Claims are checked after API signature validation.
                return "roles-verified";
            });
            app.Run();
            """);
    }

    private static async Task<string[]> VerifyDeploymentAsync(EntraDeploymentContext deployment, CancellationToken cancellationToken)
    {
        var application = await WaitForResourceAsync(
            deployment.Client, deployment.Credential, GetApplicationUrl(deployment.RegistrationNames[0]),
            resource => resource.GetProperty("appRoles").GetArrayLength() == 1, cancellationToken);
        var applicationId = application.GetProperty("appId").GetGuid();
        var role = Assert.Single(application.GetProperty("appRoles").EnumerateArray());
        Assert.Equal("Orders.Read", role.GetProperty("value").GetString());
        Assert.Equal(["Application"], role.GetProperty("allowedMemberTypes").EnumerateArray().Select(value => value.GetString()));
        using var federated = await ReadResourceAsync(
            deployment.Client, deployment.Credential,
            $"https://graph.microsoft.com/v1.0/applications/{application.GetProperty("id").GetGuid():D}/federatedIdentityCredentials",
            cancellationToken);
        Assert.NotNull(federated);
        Assert.Empty(federated.RootElement.GetProperty("value").EnumerateArray());
        var principal = await WaitForResourceAsync(
            deployment.Client, deployment.Credential,
            $"https://graph.microsoft.com/v1.0/servicePrincipals(appId='{applicationId:D}')",
            resource => resource.GetProperty("appRoles").EnumerateArray().Any(r => r.GetProperty("id").GetGuid() == role.GetProperty("id").GetGuid()),
            cancellationToken);
        var principalId = principal.GetProperty("id").GetGuid();
        var assignments = await WaitForResourceAsync(
            deployment.Client, deployment.Credential,
            $"https://graph.microsoft.com/v1.0/servicePrincipals/{principalId:D}/appRoleAssignedTo",
            resource => resource.GetProperty("value").GetArrayLength() == 2, cancellationToken);

        var resourceGroup = $"https://management.azure.com/subscriptions/{deployment.SubscriptionId}/resourceGroups/{deployment.ResourceGroupName}";
        foreach (var name in new[] { "assigned", "compute", "unassigned" })
        {
            using var containerApp = await ReadResourceAsync(
                deployment.Client, deployment.Credential,
                $"{resourceGroup}/providers/Microsoft.App/containerApps/{name}?api-version=2026-03-02-preview", cancellationToken);
            Assert.NotNull(containerApp);
            var identity = Assert.Single(containerApp.RootElement.GetProperty("identity").GetProperty("userAssignedIdentities").EnumerateObject()).Value;
            var identityPrincipalId = identity.GetProperty("principalId").GetGuid();
            var matching = assignments.GetProperty("value").EnumerateArray()
                .Where(a => a.GetProperty("principalId").GetGuid() == identityPrincipalId).ToArray();
            if (name == "unassigned")
            {
                Assert.Empty(matching);
            }
            else
            {
                var assignment = Assert.Single(matching);
                Assert.Equal(role.GetProperty("id").GetGuid(), assignment.GetProperty("appRoleId").GetGuid());
                Assert.Equal(principalId, assignment.GetProperty("resourceId").GetGuid());
            }

            var host = containerApp.RootElement.GetProperty("properties").GetProperty("configuration").GetProperty("ingress").GetProperty("fqdn").GetString();
            var url = $"https://{host}";
            await DotnetProjectDeploymentHelpers.VerifyResponseAsync(
                url, name == "unassigned" ? "unassigned-denied" : "roles-verified", cancellationToken);
            deployment.DeploymentUrls[name] = url;
        }

        return assignments.GetProperty("value").EnumerateArray()
            .Select(a => $"{a.GetProperty("principalId").GetGuid():D}/{a.GetProperty("appRoleId").GetGuid():D}/{a.GetProperty("id").GetString()}")
            .Order(StringComparer.Ordinal).ToArray();
    }
}
