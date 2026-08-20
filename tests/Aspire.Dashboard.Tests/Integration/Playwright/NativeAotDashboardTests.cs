// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Aspire.Dashboard.Otlp.Http;
using Aspire.Dashboard.Resources;
using Aspire.Tests.Shared.Telemetry;
using Aspire.TestUtilities;
using Aspire.Templates.Tests;
using Google.Protobuf;
using Microsoft.Playwright;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;
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

        var dashboardBytes = File.ReadAllBytes(dashboardPath);
        Assert.True(
            dashboardBytes.AsSpan().IndexOf("Aspire.Dashboard.Resources.Columns.resources"u8) >= 0,
            "The Native AOT Dashboard must preserve its manifest resources.");

        var workingDirectory = Directory.CreateTempSubdirectory();
        var startInfo = new ProcessStartInfo
        {
            FileName = dashboardPath,
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--ASPNETCORE_URLS=http://127.0.0.1:0");
        startInfo.ArgumentList.Add("--ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true");
        startInfo.ArgumentList.Add("--Dashboard:Otlp:GrpcEndpointUrl=http://127.0.0.1:0");
        startInfo.ArgumentList.Add("--Dashboard:Otlp:HttpEndpointUrl=http://127.0.0.1:0");
        startInfo.ArgumentList.Add("--Dashboard:Otlp:AuthMode=Unsecured");
        startInfo.Environment["ASPIRE_BUNDLE_VERSION_DIR"] = workingDirectory.FullName;

        using var dashboardProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start Native AOT Dashboard at '{dashboardPath}'.");
        var dashboardUrlSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otlpUrlSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutTask = ReadDashboardOutputAsync(dashboardProcess.StandardOutput, dashboardUrlSource, otlpUrlSource);
        var stderrTask = dashboardProcess.StandardError.ReadToEndAsync();

        try
        {
            var dashboardUrl = await WaitForDashboardAsync(dashboardUrlSource.Task, dashboardProcess);
            var leasesDirectory = Path.Combine(workingDirectory.FullName, ".leases");
            Assert.Single(Directory.GetFiles(leasesDirectory, "*.lease"));

            PlaywrightProvider.DetectAndSetInstalledPlaywrightDependenciesPath();
            await using var browser = await PlaywrightProvider.CreateBrowserAsync();
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = dashboardUrl,
                ViewportSize = new ViewportSize
                {
                    Width = 390,
                    Height = 844
                }
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

            var otlpUrl = await otlpUrlSource.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertSpanDetailsAsync(page, otlpUrl);

            var response = await page.GotoAsync("/");
            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)response.Status);
            await page.GetByRole(AriaRole.Heading, new() { Name = "Structured logs" }).WaitForAsync();

            await page.GetByTitle(Dialogs.HelpDialogCategoryNavigation).ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = Layout.MainLayoutLaunchSettings }).ClickAsync();
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

    private static async Task AssertSpanDetailsAsync(IPage page, string otlpUrl)
    {
        var startTime = DateTime.UtcNow;
        var span = TelemetryTestHelpers.CreateSpan("native-aot-trace", "aot-span", startTime, startTime.AddMilliseconds(10));
        var request = new ExportTraceServiceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    Resource = TelemetryTestHelpers.CreateResource("native-aot-service"),
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = TelemetryTestHelpers.CreateScope(),
                            Spans = { span }
                        }
                    }
                }
            }
        };
        using var client = new HttpClient { BaseAddress = new Uri(otlpUrl) };
        using var content = new ByteArrayContent(request.ToByteArray());
        content.Headers.TryAddWithoutValidation("content-type", OtlpHttpEndpointsBuilder.ProtobufContentType);
        using var response = await client.PostAsync("/v1/traces", content, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var traceId = Convert.ToHexStringLower(span.TraceId.Span);
        var spanId = Convert.ToHexStringLower(span.SpanId.Span);
        await page.GotoAsync($"/traces/detail/{traceId}");
        await page.Locator(".trace-view-grid").GetByRole(AriaRole.Row).Filter(new() { HasText = span.Name }).ClickAsync();

        var spanIdValue = page.Locator(".span-details-layout .grid-value").Filter(new() { HasText = spanId });
        await Assertions.Expect(spanIdValue).ToHaveTextAsync(spanId);
        await Assertions.Expect(spanIdValue.Locator(".severity-icon")).ToBeVisibleAsync();
    }

    private static async Task<string> WaitForDashboardAsync(Task<string> dashboardUrlTask, Process dashboardProcess)
    {
        using var client = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        try
        {
            var processExitTask = dashboardProcess.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(dashboardUrlTask, processExitTask) == processExitTask)
            {
                await processExitTask;
                throw new InvalidOperationException(
                    $"Native AOT Dashboard exited before becoming ready with exit code {dashboardProcess.ExitCode}.");
            }

            var dashboardUrl = await dashboardUrlTask;
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
                        return dashboardUrl;
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

    private static async Task<string> ReadDashboardOutputAsync(StreamReader reader, TaskCompletionSource<string> dashboardUrlSource, TaskCompletionSource<string> otlpUrlSource)
    {
        var output = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);

            // Kestrel reports the resolved ephemeral address as:
            //   Now listening on: http://127.0.0.1:54321
            //   OTLP/HTTP listening on: http://127.0.0.1:54322
            const string prefix = "Now listening on: ";
            const string otlpPrefix = "OTLP/HTTP listening on: ";
            var trimmedLine = line.TrimStart();
            if (trimmedLine.StartsWith(prefix, StringComparison.Ordinal) &&
                Uri.TryCreate(trimmedLine[prefix.Length..], UriKind.Absolute, out var dashboardUri))
            {
                dashboardUrlSource.TrySetResult(dashboardUri.AbsoluteUri.TrimEnd('/'));
            }
            else if (trimmedLine.StartsWith(otlpPrefix, StringComparison.Ordinal) &&
                Uri.TryCreate(trimmedLine[otlpPrefix.Length..], UriKind.Absolute, out var otlpUri))
            {
                otlpUrlSource.TrySetResult(otlpUri.AbsoluteUri.TrimEnd('/'));
            }
        }

        return output.ToString();
    }
}
