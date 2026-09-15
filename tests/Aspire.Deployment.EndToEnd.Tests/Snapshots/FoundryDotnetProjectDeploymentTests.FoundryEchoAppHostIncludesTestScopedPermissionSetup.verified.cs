#:sdk Aspire.AppHost.Sdk@13.6.0-dev
#pragma warning disable ASPIREDOTNETPROJECT001
#pragma warning disable ASPIREPIPELINES001
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Aspire.Hosting.Foundry;
using Aspire.Hosting.Pipelines;
using Azure.AI.Projects;
using Azure.Identity;
using System.ClientModel;

var builder = DistributedApplication.CreateBuilder(args);
// Pin both the deployment and its test-owned permission setup to the same CLI identity.
builder.Configuration["Azure:CredentialSource"] = "AzureCli";
var project = builder.AddFoundry("foundry").AddProject("project");
builder.AddDotnetProject("echo", Path.Combine("EchoAgent", "EchoAgent.csproj"))
    .AsHostedAgent(project, HostedAgentProtocol.Responses, "2.0.0");

builder.Pipeline.WithFinalAction("provision-project", async context =>
{
    var projectId = await project.Resource.Id.GetValueAsync(context.CancellationToken)
        ?? throw new InvalidOperationException("The provisioned Foundry project has no resource ID.");
    var endpoint = await project.Resource.Endpoint.GetValueAsync(context.CancellationToken)
        ?? throw new InvalidOperationException("The provisioned Foundry project has no endpoint.");
    var credential = new AzureCliCredential(new AzureCliCredentialOptions
    {
        TenantId = builder.Configuration["Azure:TenantId"]
    });
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    var permissions = new FoundryDeploymentPermissions(http, credential);
    var client = new AIProjectClient(new Uri(endpoint), credential);
    await permissions.EnsureAsync(projectId, async cancellationToken =>
    {
        try
        {
            // Probe role propagation without creating a disposable agent/version.
            await foreach (var agent in client.AgentAdministrationClient.GetAgentsAsync(cancellationToken: cancellationToken))
            {
                break;
            }

            return true;
        }
        catch (ClientResultException ex) when (ex.Status == 403)
        {
            return false;
        }
    }, context.CancellationToken);
});

builder.Build().Run();

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// This compiled helper is also appended to the generated file-based AppHost. A block namespace
// keeps its usings valid after the AppHost's top-level statements, without duplicating the code.
#pragma warning disable IDE0161
namespace Aspire.Deployment.EndToEnd.Tests.Helpers
{
    using System.Buffers.Text;
    using System.IO.Hashing;
    using System.Net.Http.Headers;
    using System.Net.Http.Json;
    using System.Text;
    using System.Text.Json;
    using Azure.Core;

    internal sealed class FoundryDeploymentPermissions(HttpClient client, TokenCredential credential)
    {
        // Azure Owner has no Foundry data actions. This role grants the deploying principal
        // agents/write, separately from the project and hosted-agent managed identities.
        // https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agent-permissions
        internal const string RoleId = "53ca6127-db72-4b80-b1b0-d745d6d5456d";
        internal const string ManagementScope = "https://management.azure.com/.default";

        internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);
        internal int MaxReadinessAttempts { get; init; } = 30;

        internal async Task EnsureAsync(
            string projectResourceId,
            Func<CancellationToken, Task<bool>> hasDataPlaneAccess,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(MaxReadinessAttempts, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(RetryDelay, TimeSpan.Zero);
            var project = new ResourceIdentifier(projectResourceId);
            if (!string.Equals(project.ResourceType.ToString(), "Microsoft.CognitiveServices/accounts/projects", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(project.SubscriptionId, out var subscriptionId))
            {
                throw new ArgumentException("Foundry test permissions must be scoped to a Foundry project.", nameof(projectResourceId));
            }
            var token = await credential.GetTokenAsync(new TokenRequestContext([ManagementScope]), cancellationToken);
            var principal = ParsePrincipal(token);
            var scope = project.ToString();
            var roleDefinitionId = $"/subscriptions/{subscriptionId:D}/providers/Microsoft.Authorization/roleDefinitions/{RoleId}";
            // The same principal/role/project tuple must reuse its assignment on retries. Keep
            // the grant inside this test's project so resource-group cleanup removes it.
            var assignmentId = new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes(
                $"{scope.ToLowerInvariant()}|{principal.ObjectId:D}|{RoleId}")));
            var assignmentUri = new Uri(
                $"https://management.azure.com{scope}/providers/Microsoft.Authorization/roleAssignments/{assignmentId:D}?api-version=2022-04-01");
            using var request = new HttpRequestMessage(HttpMethod.Put, assignmentUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Content = JsonContent.Create(new
            {
                properties = new
                {
                    roleDefinitionId,
                    principalId = principal.ObjectId,
                    principalType = principal.Type
                }
            });

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Could not assign Foundry User to deployment principal '{principal.ObjectId}' at '{scope}' (HTTP {(int)response.StatusCode}). " +
                    "The test credential must be allowed to create project-scoped role assignments.");
            }

            using var assignment = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var properties = assignment.RootElement.GetProperty("properties");
            if (!Guid.TryParse(properties.GetProperty("principalId").GetString(), out var assignedPrincipal)
                || assignedPrincipal != principal.ObjectId
                || !string.Equals(properties.GetProperty("principalType").GetString(), principal.Type, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(properties.GetProperty("roleDefinitionId").GetString(), roleDefinitionId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(properties.GetProperty("scope").GetString(), scope, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The Foundry test role assignment does not match the deployment principal, role, and project scope.");
            }

            for (var attempt = 0; attempt < MaxReadinessAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await hasDataPlaneAccess(cancellationToken))
                {
                    return;
                }

                if (attempt + 1 < MaxReadinessAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }

            throw new InvalidOperationException(
                $"Foundry User was assigned to deployment principal '{principal.ObjectId}' at '{scope}', " +
                $"but Foundry data-plane access remained forbidden after {MaxReadinessAttempts} attempts.");
        }

        internal static DeploymentPrincipal ParsePrincipal(AccessToken token)
        {
            // Entra JWTs are header.payload.signature. Only top-level identity claims belong to
            // this credential: {"oid":"<object-id>","idtyp":"app"} or {"oid":"<object-id>","upn":"user@tenant"}.
            // Do not mistake appid/azp (the client application) or a nested oid for the principal.
            // https://learn.microsoft.com/entra/identity-platform/access-token-claims-reference
            var segments = token.Token.Split('.');
            if (segments.Length != 3)
            {
                throw new InvalidOperationException("The deployment credential did not return a JWT with identity claims.");
            }

            try
            {
                using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(segments[1]));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("oid", out var oid)
                    || oid.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(oid.GetString(), out var principalId)
                    || principalId == Guid.Empty)
                {
                    throw new InvalidOperationException("The deployment credential has no valid top-level object ID.");
                }

                var hasIdentityType = root.TryGetProperty("idtyp", out var idtyp);
                if (hasIdentityType && idtyp.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException("The deployment credential has an invalid identity type.");
                }

                var identityType = hasIdentityType ? idtyp.GetString() : null;
                if (string.Equals(identityType, "app", StringComparison.OrdinalIgnoreCase))
                {
                    return new(principalId, "ServicePrincipal");
                }

                if (string.Equals(identityType, "user", StringComparison.OrdinalIgnoreCase)
                    || identityType is null && root.TryGetProperty("upn", out var upn)
                        && upn.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(upn.GetString()))
                {
                    return new(principalId, "User");
                }

                throw new InvalidOperationException("The deployment credential does not identify a user or service principal; refusing to guess the role-assignment principal type.");
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                // Token contents must not appear in failure output or captured test workspaces.
                throw new InvalidOperationException("The deployment credential has malformed JWT identity claims.");
            }
        }

        internal sealed record DeploymentPrincipal(Guid ObjectId, string Type);
    }
}
#pragma warning restore IDE0161
