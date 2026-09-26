// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDENO001 // Type is for evaluation purposes only

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Xunit.Sdk;

namespace Aspire.Hosting.JavaScript.Tests;

/// <summary>
/// Test fixture that boots an Aspire application with two <see cref="DenoAppResource"/> instances:
/// one running a script file directly (<c>deno run -A main.ts</c>) and one via a package-manager task
/// (<c>deno task start</c>).
/// </summary>
public sealed class DenoAppFixture(IMessageSink diagnosticMessageSink)
    : DenoAppFixtureBase(diagnosticMessageSink, enableDashboard: false, includeTaskApp: true);

/// <summary>
/// Test fixture that boots one Deno application and the dashboard for native telemetry validation.
/// </summary>
public sealed class DenoTelemetryFixture(IMessageSink diagnosticMessageSink)
    : DenoAppFixtureBase(diagnosticMessageSink, enableDashboard: true, includeTaskApp: false)
{
    public const string AspireDashboardResourceName = "aspire-dashboard";
    public const string DashboardApiKey = "DenoFunctionalTestsApiKey";
}

public abstract class DenoAppFixtureBase(
    IMessageSink diagnosticMessageSink,
    bool enableDashboard,
    bool includeTaskApp) : IAsyncLifetime
{
    private IDistributedApplicationTestingBuilder? _builder;
    private DistributedApplication? _app;
    private string? _denoAppPath;

    public DistributedApplication App => _app ?? throw new InvalidOperationException("DistributedApplication is not initialized.");

    public IResourceBuilder<DenoAppResource>? DenoAppBuilder { get; private set; }
    public IResourceBuilder<DenoAppResource>? DenoScriptBuilder { get; private set; }

    public async ValueTask InitializeAsync()
    {
        _builder = TestDistributedApplicationBuilder.Create(
            options =>
            {
                options.DisableDashboard = !enableDashboard;
                options.TrustDeveloperCertificate = false;
            },
            new TestOutputWrapper(diagnosticMessageSink));
        if (enableDashboard)
        {
            // Keep native telemetry validation independent of development-certificate trust.
            // Certificate environment mapping is covered separately by AddDenoAppTests.
            _builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:0";
            _builder.Configuration["AppHost:DashboardApiKey"] = DenoTelemetryFixture.DashboardApiKey;
        }

        _denoAppPath = CreateDenoApp();

        DenoAppBuilder = _builder.AddDenoApp("denoapp", _denoAppPath, "main.ts")
            .WithHttpEndpoint(env: "PORT")
            .WithHttpHealthCheck("/", endpointName: "http");

        if (includeTaskApp)
        {
            DenoScriptBuilder = _builder.AddDenoApp("denoscript", _denoAppPath, "main.ts")
                .WithRunScript("start")
                .WithHttpEndpoint(env: "PORT")
                .WithHttpHealthCheck("/", endpointName: "http");
        }

        _app = _builder.Build();

        using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await _app.StartAsync(startCts.Token);

        using var readinessCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await WaitReadyStateAsync(readinessCts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _builder?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_denoAppPath is not null)
        {
            try
            {
                Directory.Delete(_denoAppPath, recursive: true);
            }
            catch (IOException)
            {
                // A terminating Deno process can briefly retain a file handle on Windows.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup should not mask the functional-test result.
            }
        }
    }

    private static string CreateDenoApp()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-deno-tests").FullName;

        // Minimal Deno HTTP server. Distinguishes between direct (`deno run -A main.ts`) and
        // task (`deno task start`) invocations via the `--from-task` argument injected by the
        // deno.json task definition below.
        File.WriteAllText(Path.Combine(tempDir, "main.ts"),
            """
            const port = Number(Deno.env.get("PORT") ?? 3000);
            const isTaskRun = Deno.args.includes("--from-task");
            const greeting = isTaskRun ? "Hello from deno task!" : "Hello from deno!";

            Deno.serve({ port }, () => {
                console.log(`Deno telemetry request from ${isTaskRun ? "task" : "direct"} app`);
                return new Response(greeting, {
                    headers: { "Content-Type": "text/plain" },
                });
            });

            console.log(`Deno server listening on ${port}`);
            """);

        // deno.json defines the `start` task. AddDenoApp auto-detects deno.json and configures Deno as
        // the package manager, so `.WithRunScript("start")` runs `deno task start`.
        File.WriteAllText(Path.Combine(tempDir, "deno.json"),
            """
            {
              "tasks": {
                "start": "deno run -A main.ts --from-task"
              }
            }
            """);

        return tempDir;
    }

    private async Task WaitReadyStateAsync(CancellationToken cancellationToken)
    {
        // Wait for each resource in parallel — separate timeouts would compound startup time
        // and either resource being slow shouldn't starve the other.
        var readinessTasks = new List<Task>
        {
            App.ResourceNotifications.WaitForResourceHealthyAsync(DenoAppBuilder!.Resource.Name, cancellationToken)
        };
        if (enableDashboard)
        {
            readinessTasks.Add(App.ResourceNotifications.WaitForResourceAsync(
                DenoTelemetryFixture.AspireDashboardResourceName,
                KnownResourceStates.Running,
                cancellationToken));
        }
        if (DenoScriptBuilder is not null)
        {
            readinessTasks.Add(App.ResourceNotifications.WaitForResourceHealthyAsync(
                DenoScriptBuilder.Resource.Name,
                cancellationToken));
        }

        await Task.WhenAll(readinessTasks);
    }

    private sealed class TestOutputWrapper(IMessageSink messageSink) : ITestOutputHelper
    {
        public string Output => string.Empty;

        public void Write(string message)
        {
            messageSink.OnMessage(new DiagnosticMessage(message));
        }

        public void Write(string format, params object[] args)
        {
            messageSink.OnMessage(new DiagnosticMessage(string.Format(CultureInfo.CurrentCulture, format, args)));
        }

        public void WriteLine(string message)
        {
            messageSink.OnMessage(new DiagnosticMessage(message));
        }

        public void WriteLine(string format, params object[] args)
        {
            messageSink.OnMessage(new DiagnosticMessage(string.Format(CultureInfo.CurrentCulture, format, args)));
        }
    }
}
