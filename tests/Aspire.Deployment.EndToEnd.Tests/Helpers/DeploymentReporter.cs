// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Deployment.EndToEnd.Tests.Helpers;

/// <summary>
/// Reports deployment test results to GitHub Actions step summary and other outputs.
/// </summary>
internal static class DeploymentReporter
{
    // xUnit's dynamic-skip contract: a dynamically skipped test (Assert.Skip in xunit.v3) throws an
    // exception whose Message starts with this token, with the skip reason appended. We mirror the
    // literal here because xUnit's Assert.DynamicSkipToken is a private const and not referenceable.
    private const string XunitDynamicSkipToken = "$XunitDynamicSkip$";

    /// <summary>
    /// Reports a successful deployment with URLs to the GitHub step summary.
    /// </summary>
    internal static void ReportDeploymentSuccess(
        string testName,
        string resourceGroupName,
        IReadOnlyDictionary<string, string> deploymentUrls,
        TimeSpan duration)
    {
        var summaryPath = DeploymentE2ETestHelpers.GetGitHubStepSummaryPath();
        if (string.IsNullOrEmpty(summaryPath))
        {
            // Not running in CI, just log to console
            Console.WriteLine($"✅ Deployment succeeded: {testName}");
            Console.WriteLine($"   Duration: {duration}");
            Console.WriteLine($"   Resource Group: {resourceGroupName}");
            foreach (var (name, url) in deploymentUrls)
            {
                Console.WriteLine($"   {name}: {url}");
            }
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## ✅ Deployment Succeeded");
        sb.AppendLine();
        sb.AppendLine($"**Test:** {testName}");
        sb.AppendLine($"**Duration:** {duration:hh\\:mm\\:ss}");
        sb.AppendLine($"**Resource Group:** `{resourceGroupName}`");
        sb.AppendLine();

        if (deploymentUrls.Count > 0)
        {
            sb.AppendLine("### Deployed Resources");
            sb.AppendLine();
            sb.AppendLine("| Resource | URL |");
            sb.AppendLine("|----------|-----|");
            foreach (var (name, url) in deploymentUrls)
            {
                sb.AppendLine($"| {name} | [{url}]({url}) |");
            }
            sb.AppendLine();
        }

        var azurePortalUrl = $"https://portal.azure.com/#@/resource/subscriptions/{AzureAuthenticationHelpers.TryGetSubscriptionId()}/resourceGroups/{resourceGroupName}/overview";
        sb.AppendLine($"[View in Azure Portal]({azurePortalUrl})");
        sb.AppendLine();

        File.AppendAllText(summaryPath, sb.ToString());
    }

    /// <summary>
    /// Reports a failed deployment to the GitHub step summary.
    /// </summary>
    internal static void ReportDeploymentFailure(
        string testName,
        string resourceGroupName,
        string errorMessage,
        string? logs = null)
    {
        // A transient Azure-capacity Assert.Skip (see WaitForPipelineSuccessAsync) flows through the
        // same generic `catch (Exception)` that deployment tests use to report real failures. Detect
        // the dynamic-skip token so a skip is not reported as a deployment failure in the step summary
        // — the run already records the test as skipped, not failed.
        if (errorMessage.StartsWith(XunitDynamicSkipToken, StringComparison.Ordinal))
        {
            return;
        }

        var summaryPath = DeploymentE2ETestHelpers.GetGitHubStepSummaryPath();
        if (string.IsNullOrEmpty(summaryPath))
        {
            // Not running in CI, just log to console
            Console.WriteLine($"❌ Deployment failed: {testName}");
            Console.WriteLine($"   Resource Group: {resourceGroupName}");
            Console.WriteLine($"   Error: {errorMessage}");
            if (!string.IsNullOrEmpty(logs))
            {
                Console.WriteLine($"   Logs: {logs}");
            }
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## ❌ Deployment Failed");
        sb.AppendLine();
        sb.AppendLine($"**Test:** {testName}");
        sb.AppendLine($"**Resource Group:** `{resourceGroupName}`");
        sb.AppendLine();
        sb.AppendLine("### Error");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(errorMessage);
        sb.AppendLine("```");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(logs))
        {
            sb.AppendLine("<details>");
            sb.AppendLine("<summary>Full Logs</summary>");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(logs);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        var subscriptionId = AzureAuthenticationHelpers.TryGetSubscriptionId();
        if (!string.IsNullOrEmpty(subscriptionId))
        {
            var azurePortalUrl = $"https://portal.azure.com/#@/resource/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/overview";
            sb.AppendLine($"[View Resource Group in Azure Portal]({azurePortalUrl})");
            sb.AppendLine();
        }

        File.AppendAllText(summaryPath, sb.ToString());
    }

    /// <summary>
    /// Reports resource cleanup status to the GitHub step summary.
    /// </summary>
    internal static void ReportCleanupStatus(string resourceGroupName, bool success, string? errorMessage = null)
    {
        var summaryPath = DeploymentE2ETestHelpers.GetGitHubStepSummaryPath();
        if (string.IsNullOrEmpty(summaryPath))
        {
            if (success)
            {
                Console.WriteLine($"🧹 Cleanup completed: {resourceGroupName}");
            }
            else
            {
                Console.WriteLine($"⚠️ Cleanup failed: {resourceGroupName} - {errorMessage}");
            }
            return;
        }

        var sb = new StringBuilder();
        if (success)
        {
            sb.AppendLine($"### 🧹 Cleanup");
            sb.AppendLine();
            sb.AppendLine($"Resource group `{resourceGroupName}` deleted successfully.");
        }
        else
        {
            sb.AppendLine($"### ⚠️ Cleanup Warning");
            sb.AppendLine();
            sb.AppendLine($"Failed to delete resource group `{resourceGroupName}`.");
            if (!string.IsNullOrEmpty(errorMessage))
            {
                sb.AppendLine($"Error: {errorMessage}");
            }
            sb.AppendLine();
            sb.AppendLine("Manual cleanup may be required.");
        }
        sb.AppendLine();

        File.AppendAllText(summaryPath, sb.ToString());
    }
}
