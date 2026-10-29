// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Azure.Core;
using Xunit;
using static Aspire.Deployment.EndToEnd.Tests.Helpers.EntraDeploymentHelpers;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AzureEntraIdDeploymentTests(ITestOutputHelper output)
{
    private const string RedirectUri = "https://example.invalid/signin-oidc";
    private const string UpdatedRedirectUri = "https://example.invalid/updated-signin-oidc";
    private const string ParameterRedirectUris = "https://example.invalid/signout-oidc,https://example.invalid/callback";
    private const string UpdatedParameterRedirectUris = "https://example.invalid/updated-callback";
    private const string EndpointCallbackPath = "/endpoint-callback";

    [Fact]
    public Task DeployEntraRegistrationAndRedeployWithStableIds() =>
        RunAsync(
            output,
            nameof(DeployEntraRegistrationAndRedeployWithStableIds),
            "entra",
            suffix => [$"identity-{suffix}"],
            async (deployment, auto, counter, cancellationToken) =>
            {
                var registrationName = deployment.RegistrationNames[0];
                await auto.PrepareEnvironmentAsync(deployment.Workspace, counter);
                await auto.InstallAspireCliAsync(deployment.Strategy, counter, output);
                await auto.AspireNewAsync("EntraDeployment", counter, template: AspireTemplate.EmptyAppHost);
                await auto.RunCommandAsync("cd EntraDeployment", counter);
                await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.AppContainers");
                await DotnetProjectDeploymentHelpers.AddPackageAsync(auto, counter, "Aspire.Hosting.Azure.EntraId");
                await auto.RunCommandAsync("dotnet new web -n Consumer --no-restore", counter, TimeSpan.FromMinutes(2));
                var projectDirectory = Path.Combine(deployment.Workspace.Path, "EntraDeployment");
                WriteApplication(projectDirectory);

                await deployment.ConfigureAzureEnvironmentAsync(auto, counter);

                await auto.RunCommandAsync(
                    $"export Parameters__redirecturis={AspireCliShellCommandHelpers.QuoteBashArg(ParameterRedirectUris)}", counter);

                deployment.DeploymentAttempted = true;
                await DeployAsync(auto, counter);
                var firstIds = await VerifyDeploymentAsync(
                    deployment.Client, deployment.Credential, deployment.SubscriptionId, deployment.ResourceGroupName, registrationName, deployment.TenantId, [RedirectUri, .. ParameterRedirectUris.Split(',')], deployment.DeploymentUrls, cancellationToken);

                // Change actual Graph settings, including the deployment-supplied parameter, so the second deployment
                // must update the registration rather than only demonstrating that a cached deployment preserves its IDs.
                await auto.RunCommandAsync(
                    $"export Parameters__redirecturis={AspireCliShellCommandHelpers.QuoteBashArg(UpdatedParameterRedirectUris)}", counter);
                DotnetProjectDeploymentHelpers.ReplaceInFile(
                    Path.Combine(projectDirectory, "apphost.cs"), RedirectUri, UpdatedRedirectUri);
                await DeployAsync(auto, counter);
                var secondIds = await VerifyDeploymentAsync(
                    deployment.Client, deployment.Credential, deployment.SubscriptionId, deployment.ResourceGroupName, registrationName, deployment.TenantId, [UpdatedRedirectUri, .. UpdatedParameterRedirectUris.Split(',')], deployment.DeploymentUrls, cancellationToken);
                Assert.Equal(firstIds, secondIds);
            });

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
}
