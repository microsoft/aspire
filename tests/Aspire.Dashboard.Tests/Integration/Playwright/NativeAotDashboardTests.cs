// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aspire.Dashboard.Resources;
using Aspire.TestUtilities;
using Aspire.Templates.Tests;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

public class NativeAotDashboardTests(ITestOutputHelper outputHelper)
{
    [Fact]
    [OuterloopTest("Publishes and launches a Native AOT executable with a browser.")]
    public async Task NativeDashboard_LoadsInteractivePageWithoutBrowserErrors()
    {
        var dashboardPath = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_DASHBOARD_PATH");
        Assert.SkipWhen(
            string.IsNullOrEmpty(dashboardPath),
            "ASPIRE_NATIVE_DASHBOARD_PATH must identify the Native AOT Dashboard executable.");

        var workingDirectory = Directory.CreateTempSubdirectory();
        var dashboardUrl = $"http://127.0.0.1:{GetFreePort()}";
        var startInfo = new ProcessStartInfo
        {
            FileName = dashboardPath,
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"--ASPNETCORE_URLS={dashboardUrl}");
        startInfo.ArgumentList.Add("--ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true");
        startInfo.Environment["ASPIRE_BUNDLE_VERSION_DIR"] = workingDirectory.FullName;

        using var dashboardProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start Native AOT Dashboard at '{dashboardPath}'.");
        var stdoutTask = dashboardProcess.StandardOutput.ReadToEndAsync();
        var stderrTask = dashboardProcess.StandardError.ReadToEndAsync();

        try
        {
            await WaitForDashboardAsync(dashboardUrl, dashboardProcess);
            var leasesDirectory = Path.Combine(workingDirectory.FullName, ".leases");
            Assert.Single(Directory.GetFiles(leasesDirectory, "*.lease"));

            PlaywrightProvider.DetectAndSetInstalledPlaywrightDependenciesPath();
            await using var browser = await PlaywrightProvider.CreateBrowserAsync();
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = dashboardUrl
            });
            var page = await context.NewPageAsync();
            var browserErrors = new ConcurrentQueue<string>();
            page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    browserErrors.Enqueue(message.Text);
                }
            };
            page.PageError += (_, error) => browserErrors.Enqueue(error);

            var response = await page.GotoAsync("/");
            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)response.Status);
            await page.GetByRole(AriaRole.Heading, new() { Name = "Structured logs" }).WaitForAsync();

            await page.GetByRole(AriaRole.Button, new() { Name = Layout.MainLayoutLaunchSettings }).ClickAsync();
            var darkThemeLabel = page.GetByText(Dialogs.SettingsDialogDarkTheme, new() { Exact = true }).First;
            var darkThemeRadioId = await darkThemeLabel.GetAttributeAsync("for");
            Assert.False(string.IsNullOrEmpty(darkThemeRadioId));
            await page.Locator($"fluent-radio[id='{darkThemeRadioId}']").ClickAsync();
            await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");

            await page.WaitForTimeoutAsync(1_000);

            Assert.True(browserErrors.IsEmpty, string.Join(Environment.NewLine, browserErrors));
        }
        finally
        {
            if (!dashboardProcess.HasExited)
            {
                dashboardProcess.Kill(entireProcessTree: true);
            }

            await dashboardProcess.WaitForExitAsync();
            outputHelper.WriteLine("Dashboard stdout:");
            outputHelper.WriteLine(await stdoutTask);
            outputHelper.WriteLine("Dashboard stderr:");
            outputHelper.WriteLine(await stderrTask);
            var leasesDirectory = Path.Combine(workingDirectory.FullName, ".leases");
            if (Directory.Exists(leasesDirectory))
            {
                Assert.Empty(Directory.GetFiles(leasesDirectory, "*.lease"));
            }
            workingDirectory.Delete(recursive: true);
        }
    }

    private static async Task WaitForDashboardAsync(string dashboardUrl, Process dashboardProcess)
    {
        using var client = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        try
        {
            while (true)
            {
                if (dashboardProcess.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Native AOT Dashboard exited before becoming ready with exit code {dashboardProcess.ExitCode}.");
                }

                try
                {
                    using var response = await client.GetAsync(dashboardUrl, timeout.Token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Kestrel can take a moment to bind after the process starts.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for the Native AOT Dashboard.");
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
