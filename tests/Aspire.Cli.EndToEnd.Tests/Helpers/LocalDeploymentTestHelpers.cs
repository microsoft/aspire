// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

internal static class LocalDeploymentTestHelpers
{
    internal const string DeploymentLabel = "aspire.e2e.deployment";

    internal static void WriteReferencingWebProgram(string projectDir, string projectName, string apiResourceName)
    {
        var webDirectory = Path.Combine(projectDir, $"{projectName}.Web");
        Assert.True(File.Exists(Path.Combine(webDirectory, $"{projectName}.Web.csproj")));
        Assert.True(File.Exists(Path.Combine(webDirectory, "Properties", "launchSettings.json")));
        var programPath = Path.Combine(webDirectory, "Program.cs");
        Assert.True(File.Exists(programPath));

        // Keep the template projects and launch metadata; replace only the HTTP behavior.
        // The named client must resolve Aspire's injected reference, not a hard-coded host.
        File.WriteAllText(programPath, $$"""
            var builder = WebApplication.CreateBuilder(args);
            builder.AddServiceDefaults();
            builder.Services.AddHttpClient("api", client =>
            {
                client.BaseAddress = new Uri("http://{{apiResourceName}}");
                client.Timeout = TimeSpan.FromSeconds(3);
            });

            var app = builder.Build();
            app.MapDefaultEndpoints();
            app.MapGet("/test-deployment", async (IHttpClientFactory clients) =>
                Results.Text("WEB->" + await clients.CreateClient("api").GetStringAsync("/test-deployment")));
            app.Run();
            """);
    }

    internal static async Task CleanupLabeledDockerResourcesAsync(string label, ITestOutputHelper output)
    {
        foreach (var kind in new[] { "container", "volume", "network" })
        {
            var resources = await RunDockerCleanupAsync([kind, "ls", "-q", "--filter", $"label={label}"], output);
            foreach (var resource in resources.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string[] arguments = kind == "container"
                    ? [kind, "rm", "-f", "-v", resource]
                    : [kind, "rm", resource];
                await RunDockerCleanupAsync(arguments, output);
            }
        }
    }

    internal static async Task CleanupImageAsync(string image, ITestOutputHelper output)
    {
        await RunDockerCleanupAsync(["image", "rm", image], output);
    }

    private static async Task<string> RunDockerCleanupAsync(string[] arguments, ITestOutputHelper output)
    {
        try
        {
            // Cleanup must still run when the test's cancellation token has been cancelled.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("docker")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var result = await stdout;
                var error = await stderr;
                if (process.ExitCode != 0)
                {
                    output.WriteLine($"Cleanup warning: docker {string.Join(' ', arguments)} failed (exit {process.ExitCode}): {error}");
                    return "";
                }

                output.WriteLine($"Cleanup: docker {string.Join(' ', arguments)} succeeded.");
                return result;
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
            // Only cleanup process/IO failures are best-effort. This helper is private so
            // setup, deployment and verification commands cannot accidentally use it.
            output.WriteLine($"Cleanup warning: docker {string.Join(' ', arguments)} failed ({ex.GetType().Name}): {ex.Message}");
            return "";
        }
    }
}
