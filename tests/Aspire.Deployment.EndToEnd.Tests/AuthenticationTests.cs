// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// Tests that validate Azure authentication is working correctly.
/// These tests run first to fail fast if authentication is not configured.
/// </summary>
public sealed class AuthenticationTests(ITestOutputHelper output)
{
    [Fact]
    public void SubscriptionIdIsConfigured()
    {
        var subscriptionId = AzureAuthenticationHelpers.TryGetSubscriptionId();

        if (string.IsNullOrEmpty(subscriptionId))
        {
            output.WriteLine("⚠️ Azure subscription ID is not configured.");
            output.WriteLine("Set ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION or AZURE_SUBSCRIPTION_ID environment variable.");
            output.WriteLine("");
            output.WriteLine("For local development:");
            output.WriteLine("  export ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION=\"your-subscription-id\"");
            output.WriteLine("");
            Assert.Skip("Azure subscription ID not configured. Set ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION environment variable.");
        }

        output.WriteLine($"✅ Subscription ID configured: {subscriptionId[..8]}...");
        Assert.False(string.IsNullOrEmpty(subscriptionId));
    }

    [Fact]
    public void AzureCredentialsAreAvailable()
    {
        // Skip if subscription isn't configured (no point testing auth without a target)
        var subscriptionId = AzureAuthenticationHelpers.TryGetSubscriptionId();
        if (string.IsNullOrEmpty(subscriptionId))
        {
            Assert.Skip("Subscription not configured - skipping auth test.");
        }

        var isAuthAvailable = AzureAuthenticationHelpers.IsAzureAuthAvailable();

        if (!isAuthAvailable)
        {
            output.WriteLine("⚠️ Azure authentication is not available.");
            output.WriteLine("");
            output.WriteLine("For local development, authenticate with Azure CLI:");
            output.WriteLine("  az login");
            output.WriteLine("  az account set --subscription \"your-subscription-id\"");
            output.WriteLine("");
            output.WriteLine("For CI, ensure OIDC is configured:");
            output.WriteLine("  - AZURE_CLIENT_ID is set");
            output.WriteLine("  - AZURE_TENANT_ID is set");
            output.WriteLine("  - Workload Identity Federation is configured");
            output.WriteLine("");

            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                // In CI, this should fail - auth must be configured
                Assert.Fail("Azure authentication not available in CI. Check OIDC configuration.");
            }
            else
            {
                // Locally, skip with helpful message
                Assert.Skip("Azure authentication not available. Run 'az login' to authenticate.");
            }
        }

        output.WriteLine("✅ Azure credentials are available.");

        if (AzureAuthenticationHelpers.IsOidcConfigured())
        {
            var clientId = AzureAuthenticationHelpers.GetClientId();
            output.WriteLine($"   Using OIDC authentication (Client ID: {clientId?[..Math.Min(8, clientId?.Length ?? 0)]}...)");
        }
        else
        {
            output.WriteLine("   Using Azure CLI authentication");
        }
    }

    [Fact]
    public void ResourceGroupNameGenerationWorks()
    {
        var rgName = AzureAuthenticationHelpers.GenerateResourceGroupName("TestScenario");

        output.WriteLine($"Generated resource group name: {rgName}");

        Assert.NotNull(rgName);
        Assert.StartsWith(AzureAuthenticationHelpers.GetResourceGroupPrefix(), rgName);
        // The test name is hashed to create a short identifier in the resource group name
        // So we verify the format rather than looking for the literal test name

        // Verify it's a valid Azure resource group name
        Assert.True(rgName.Length <= 90, "Resource group name must be <= 90 characters");
        Assert.Matches(@"^[a-zA-Z0-9\-_\.]+$", rgName);

        // Verify the format: {prefix}-{hash}-{timestamp}-{suffix}
        var parts = rgName.Split('-');
        Assert.True(parts.Length >= 4, $"Expected at least 4 parts in name, got {parts.Length}: {rgName}");

        output.WriteLine("✅ Resource group name generation works correctly.");
    }

    [Fact]
    public void EnvironmentDetectionWorks()
    {
        var isCI = DeploymentE2ETestHelpers.IsRunningInCI;
        var prNumber = DeploymentE2ETestHelpers.GetPrNumber();
        var commitSha = DeploymentE2ETestHelpers.GetCommitSha();

        output.WriteLine($"Running in CI: {isCI}");
        output.WriteLine($"PR Number: {prNumber}");
        output.WriteLine($"Commit SHA: {commitSha}");

        if (isCI)
        {
            output.WriteLine("✅ CI environment detected.");
        }
        else
        {
            output.WriteLine("✅ Local development environment detected.");
            Assert.Equal(0, prNumber);
            Assert.Equal("local0000", commitSha);
        }
    }
}
