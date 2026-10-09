// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests.Helpers;

internal static class DotnetProjectDeploymentHelpers
{
    internal static string GetSubscriptionId()
    {
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

        return subscriptionId;
    }

    internal static async Task AddPackageAsync(Hex1bTerminalAutomator auto, SequenceCounter counter, string packageName)
    {
        // aspire add resolves through the installed CLI's channel/local hive, not nuget.org's latest release.
        await auto.TypeAsync($"aspire add {packageName}");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));
    }

    internal static string ReplaceExactlyOnce(string content, string oldValue, string newValue)
    {
        var index = content.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0 || content.IndexOf(oldValue, index + oldValue.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException($"Expected exactly one '{oldValue}' in the generated source.");
        }

        return content.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    internal static void ReplaceInFile(string path, string oldValue, string newValue)
    {
        File.WriteAllText(path, ReplaceExactlyOnce(File.ReadAllText(path), oldValue, newValue));
    }

    internal static Task RunScriptAsync(Hex1bTerminalAutomator auto, SequenceCounter counter, string script, TimeSpan timeout)
    {
        return auto.RunCommandAsync($"bash -c {AspireCliShellCommandHelpers.QuoteBashArg(script)}", counter, timeout);
    }

    internal static async Task VerifyResponseAsync(
        string url,
        string expected,
        CancellationToken cancellationToken,
        AuthenticationHeaderValue? authorization = null)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        string? lastResult = null;
        for (var attempt = 0; attempt < 18; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = authorization;
                using var response = await client.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK && body == expected)
                {
                    return;
                }

                lastResult = $"HTTP {(int)response.StatusCode}, body: {body}";
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                lastResult = ex.Message;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }

        Assert.Fail($"The deployed workload at {url} did not return '{expected}'. Last result: {lastResult}");
    }

    internal static async Task CleanupAsync(string resourceGroupName, string subscriptionId, ITestOutputHelper output)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
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
            foreach (var argument in new[] { "group", "delete", "--name", resourceGroupName, "--subscription", subscriptionId, "--yes", "--no-wait" })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
                await stdout;
                var error = await stderr;
                DeploymentReporter.ReportCleanupStatus(resourceGroupName, process.ExitCode == 0,
                    process.ExitCode == 0 ? "Resource group deletion requested." : error);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            output.WriteLine($"Cleanup failed for {resourceGroupName}: {ex.Message}");
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, false, ex.Message);
        }
    }
}
