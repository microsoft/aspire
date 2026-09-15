// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Azure.Core;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed partial class FoundryDotnetProjectDeploymentTests
{
    private const string TestPrincipalId = "55b498a7-c920-47ab-b92c-253a29d2c832";
    private const string TestProjectId = "/subscriptions/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/resourceGroups/test-foundry/providers/Microsoft.CognitiveServices/accounts/foundry/projects/project";
    private const string TestRoleDefinitionId = "/subscriptions/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/providers/Microsoft.Authorization/roleDefinitions/53ca6127-db72-4b80-b1b0-d745d6d5456d";

    [Fact]
    public async Task FoundryEchoAppHostIncludesTestScopedPermissionSetup()
    {
        var source = FoundryEchoTestApp.CreateAppHost("#:sdk Aspire.AppHost.Sdk@13.6.0-dev");
        await Verify(source, "cs");
    }

    [Theory]
    [InlineData("""{"oid":"55b498a7-c920-47ab-b92c-253a29d2c832","idtyp":"app","appid":"different-client-id"}""", "ServicePrincipal")]
    [InlineData("""{"oid":"55b498a7-c920-47ab-b92c-253a29d2c832","idtyp":"APP","upn":"application-display-name"}""", "ServicePrincipal")]
    [InlineData("""{"oid":"55b498a7-c920-47ab-b92c-253a29d2c832","idtyp":"user"}""", "User")]
    [InlineData("""{"oid":"55b498a7-c920-47ab-b92c-253a29d2c832","upn":"user@example.test"}""", "User")]
    public void FoundryPermissionsResolveTheDeploymentObjectId(string payload, string expectedType)
    {
        var principal = FoundryDeploymentPermissions.ParsePrincipal(CreateTestToken(payload));
        Assert.Equal(Guid.Parse(TestPrincipalId), principal.ObjectId);
        Assert.Equal(expectedType, principal.Type);
    }

    [Theory]
    [InlineData("""{"appid":"55b498a7-c920-47ab-b92c-253a29d2c832","idtyp":"app"}""")]
    [InlineData("""{"act":{"oid":"55b498a7-c920-47ab-b92c-253a29d2c832"},"idtyp":"app"}""")]
    [InlineData("""{"oid":"00000000-0000-0000-0000-000000000000","idtyp":"app"}""")]
    [InlineData("""{"oid":17,"idtyp":"app"}""")]
    [InlineData("""{"oid":"55b498a7-c920-47ab-b92c-253a29d2c832"}""")]
    [InlineData("""{"oid":"55b498a7-c920-47ab-b92c-253a29d2c832","idtyp":"unknown","upn":"user@example.test"}""")]
    [InlineData("""{"oid":"55b498a7-c920-47ab-b92c-253a29d2c832","idtyp":false,"upn":"user@example.test"}""")]
    [InlineData("[]")]
    [InlineData("{invalid")]
    public void FoundryPermissionsRejectMissingOrAmbiguousIdentity(string payload)
    {
        Assert.Throws<InvalidOperationException>(() => FoundryDeploymentPermissions.ParsePrincipal(CreateTestToken(payload)));
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("header.not-base64!.signature")]
    public void FoundryPermissionsRejectMalformedTokensWithoutDisclosingThem(string token)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            FoundryDeploymentPermissions.ParsePrincipal(new AccessToken(token, DateTimeOffset.MaxValue)));
        Assert.Equal(token.Contains('.', StringComparison.Ordinal)
            ? "The deployment credential has malformed JWT identity claims."
            : "The deployment credential did not return a JWT with identity claims.", exception.Message);
    }

    [Fact]
    public async Task FoundryPermissionsAssignOnlyTheProjectScopedRoleBeforeCheckingAccess()
    {
        var credential = CreateTestCredential();
        using var handler = new TestDeploymentHttpMessageHandler((_, _) => Task.FromResult(CreateRoleAssignmentResponse()));
        using var client = new HttpClient(handler);
        var permissions = new FoundryDeploymentPermissions(client, credential);
        var checkedAccess = false;
        await permissions.EnsureAsync(TestProjectId, _ =>
        {
            Assert.Single(handler.Requests);
            checkedAccess = true;
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);

        Assert.True(checkedAccess);
        Assert.Equal([FoundryDeploymentPermissions.ManagementScope], Assert.Single(credential.RequestedScopes));
        await Verify(handler.Requests.ToArray()).DontScrubGuids();
    }

    [Fact]
    public async Task FoundryPermissionsSupportTheExplicitUserPrincipalType()
    {
        var credential = new TestDeploymentTokenCredential(CreateTestToken($$"""{"oid":"{{TestPrincipalId}}","idtyp":"user"}"""));
        using var handler = new TestDeploymentHttpMessageHandler(async (request, cancellationToken) =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("User", body.RootElement.GetProperty("properties").GetProperty("principalType").GetString());
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new
                {
                    properties = new
                    {
                        principalId = TestPrincipalId,
                        principalType = "User",
                        roleDefinitionId = TestRoleDefinitionId,
                        scope = TestProjectId
                    }
                })
            };
        });
        using var client = new HttpClient(handler);
        await new FoundryDeploymentPermissions(client, credential)
            .EnsureAsync(TestProjectId, _ => Task.FromResult(true), TestContext.Current.CancellationToken);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FoundryPermissionsReuseTheAssignmentOnRetry()
    {
        using var handler = new TestDeploymentHttpMessageHandler((_, _) => Task.FromResult(CreateRoleAssignmentResponse()));
        using var client = new HttpClient(handler);
        var permissions = new FoundryDeploymentPermissions(client, CreateTestCredential());
        await permissions.EnsureAsync(TestProjectId, _ => Task.FromResult(true), TestContext.Current.CancellationToken);
        await permissions.EnsureAsync(TestProjectId, _ => Task.FromResult(true), TestContext.Current.CancellationToken);
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0], requests[1]);
    }

    [Fact]
    public async Task FoundryPermissionsWaitForPropagationWithoutRepeatingTheAssignment()
    {
        using var handler = new TestDeploymentHttpMessageHandler((_, _) => Task.FromResult(CreateRoleAssignmentResponse()));
        using var client = new HttpClient(handler);
        var permissions = new FoundryDeploymentPermissions(client, CreateTestCredential())
        {
            RetryDelay = TimeSpan.Zero,
            MaxReadinessAttempts = 3
        };
        var attempts = 0;
        await permissions.EnsureAsync(TestProjectId, _ => Task.FromResult(++attempts == 3), TestContext.Current.CancellationToken);
        Assert.Equal(3, attempts);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FoundryPermissionsFailAfterBoundedPropagationAttempts()
    {
        using var handler = new TestDeploymentHttpMessageHandler((_, _) => Task.FromResult(CreateRoleAssignmentResponse()));
        using var client = new HttpClient(handler);
        var permissions = new FoundryDeploymentPermissions(client, CreateTestCredential())
        {
            RetryDelay = TimeSpan.Zero,
            MaxReadinessAttempts = 3
        };
        var attempts = 0;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            permissions.EnsureAsync(TestProjectId, _ =>
            {
                attempts++;
                return Task.FromResult(false);
            }, TestContext.Current.CancellationToken));
        Assert.Equal(3, attempts);
        Assert.Single(handler.Requests);
        Assert.Equal(
            $"Foundry User was assigned to deployment principal '{TestPrincipalId}' at '{TestProjectId}', but Foundry data-plane access remained forbidden after 3 attempts.",
            exception.Message);
    }

    [Fact]
    public async Task FoundryPermissionsHonorCancellationDuringPropagation()
    {
        using var handler = new TestDeploymentHttpMessageHandler((_, _) => Task.FromResult(CreateRoleAssignmentResponse()));
        using var client = new HttpClient(handler);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var permissions = new FoundryDeploymentPermissions(client, CreateTestCredential());
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => permissions.EnsureAsync(TestProjectId, _ =>
        {
            attempts++;
            cts.Cancel();
            return Task.FromResult(false);
        }, cts.Token));
        Assert.Equal(1, attempts);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FoundryPermissionsDoNotHideProbeFailures()
    {
        using var handler = new TestDeploymentHttpMessageHandler((_, _) => Task.FromResult(CreateRoleAssignmentResponse()));
        using var client = new HttpClient(handler);
        var permissions = new FoundryDeploymentPermissions(client, CreateTestCredential());
        var failure = new HttpRequestException("Permission probe failed.");
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            permissions.EnsureAsync(TestProjectId, _ => throw failure, TestContext.Current.CancellationToken));
        Assert.Same(failure, exception);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task FoundryPermissionsStopWhenAssignmentFails(HttpStatusCode status)
    {
        using var handler = new TestDeploymentHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var client = new HttpClient(handler);
        var permissions = new FoundryDeploymentPermissions(client, CreateTestCredential());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            permissions.EnsureAsync(TestProjectId, _ => throw new InvalidOperationException("Must not probe."), TestContext.Current.CancellationToken));
        Assert.Equal(
            $"Could not assign Foundry User to deployment principal '{TestPrincipalId}' at '{TestProjectId}' (HTTP {(int)status}). " +
            "The test credential must be allowed to create project-scoped role assignments.",
            exception.Message);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("principalId")]
    [InlineData("principalType")]
    [InlineData("roleDefinitionId")]
    [InlineData("scope")]
    public async Task FoundryPermissionsRejectAnUnexpectedAssignment(string property)
    {
        using var handler = new TestDeploymentHttpMessageHandler((_, _) =>
        {
            var properties = new Dictionary<string, string>
            {
                ["principalId"] = TestPrincipalId,
                ["principalType"] = "ServicePrincipal",
                ["roleDefinitionId"] = TestRoleDefinitionId,
                ["scope"] = TestProjectId
            };
            properties[property] = "unexpected";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { properties }) });
        });
        using var client = new HttpClient(handler);
        var permissions = new FoundryDeploymentPermissions(client, CreateTestCredential());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            permissions.EnsureAsync(TestProjectId, _ => throw new InvalidOperationException("Must not probe."), TestContext.Current.CancellationToken));
        Assert.Equal("The Foundry test role assignment does not match the deployment principal, role, and project scope.", exception.Message);
    }

    [Theory]
    [InlineData("/subscriptions/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("/subscriptions/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/resourceGroups/test-foundry")]
    [InlineData("/subscriptions/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/resourceGroups/test-foundry/providers/Microsoft.CognitiveServices/accounts/foundry")]
    public async Task FoundryPermissionsRejectBroaderScopes(string scope)
    {
        var credential = CreateTestCredential();
        using var handler = new TestDeploymentHttpMessageHandler((_, _) => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var permissions = new FoundryDeploymentPermissions(client, credential);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            permissions.EnsureAsync(scope, _ => throw new InvalidOperationException("Must not probe."), TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
        Assert.Empty(credential.RequestedScopes);
    }

    private static TestDeploymentTokenCredential CreateTestCredential()
        => new(CreateTestToken($$"""{"oid":"{{TestPrincipalId}}","idtyp":"app"}"""));

    private static AccessToken CreateTestToken(string payload)
        => new($"e30.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}.signature", DateTimeOffset.MaxValue);

    private static HttpResponseMessage CreateRoleAssignmentResponse()
        => new(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new
            {
                properties = new
                {
                    principalId = TestPrincipalId,
                    principalType = "ServicePrincipal",
                    roleDefinitionId = TestRoleDefinitionId,
                    scope = TestProjectId
                }
            })
        };
}
