// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Xunit.Sdk;

namespace Aspire.Hosting.JavaScript.Tests;

/// <summary>
/// Test fixture that boots an Aspire application with two <see cref="BunAppResource"/> instances:
/// one running a script file directly (<c>bun server.ts</c>) and one via a package-manager script
/// (<c>bun run start</c>).
/// </summary>
public class BunAppFixture(IMessageSink diagnosticMessageSink) : IAsyncLifetime
{
    private IDistributedApplicationTestingBuilder? _builder;
    private DistributedApplication? _app;
    private string? _bunAppPath;

    public DistributedApplication App => _app ?? throw new InvalidOperationException("DistributedApplication is not initialized.");

    public IResourceBuilder<BunAppResource>? BunAppBuilder { get; private set; }
    public IResourceBuilder<BunAppResource>? BunScriptBuilder { get; private set; }

    public async ValueTask InitializeAsync()
    {
        _builder = TestDistributedApplicationBuilder.Create()
            .WithTestAndResourceLogging(new TestOutputWrapper(diagnosticMessageSink));

        _bunAppPath = CreateBunApp();

        BunAppBuilder = _builder.AddBunApp("bunapp", _bunAppPath, "server.ts")
            .WithHttpEndpoint(env: "PORT")
            .WithHttpHealthCheck("/", endpointName: "http");

        BunScriptBuilder = _builder.AddBunApp("bunscript", _bunAppPath, "server.ts")
            .WithRunScript("start")
            .WithHttpEndpoint(env: "PORT")
            .WithHttpHealthCheck("/", endpointName: "http");

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

        if (_bunAppPath is not null)
        {
            try
            {
                Directory.Delete(_bunAppPath, recursive: true);
            }
            catch
            {
                // Don't fail the test if we can't clean up the temporary folder
            }
        }
    }

    private static string CreateBunApp()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-bun-tests").FullName;

        // Minimal Bun HTTP server. Distinguishes between direct (`bun server.ts`) and
        // script (`bun run start`) invocations via `npm_lifecycle_event`, which Bun sets
        // for script-based invocations: https://bun.com/docs/cli/run
        File.WriteAllText(Path.Combine(tempDir, "server.ts"),
            """
            const port = Number(process.env.PORT ?? 3000);
            const isScriptRun = process.env.npm_lifecycle_event !== undefined;
            const greeting = isScriptRun ? "Hello from bun script!" : "Hello from bun!";

            Bun.serve({
                port,
                fetch() {
                    return new Response(greeting, {
                        headers: { "Content-Type": "text/plain" },
                    });
                },
            });

            console.log(`Bun server listening on ${port}`);
            """);

        File.WriteAllText(Path.Combine(tempDir, "package.json"),
            """
            {
              "name": "bun-fixture",
              "version": "1.0.0",
              "type": "module",
              "private": true,
              "scripts": {
                "start": "bun server.ts"
              }
            }
            """);

        return tempDir;
    }

    private async Task WaitReadyStateAsync(CancellationToken cancellationToken)
    {
        // Wait for each resource in parallel — separate timeouts would compound startup time
        // and either resource being slow shouldn't starve the other.
        await Task.WhenAll(
            App.ResourceNotifications.WaitForResourceHealthyAsync(BunAppBuilder!.Resource.Name, cancellationToken),
            App.ResourceNotifications.WaitForResourceHealthyAsync(BunScriptBuilder!.Resource.Name, cancellationToken));
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
