// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using NativeIntegrations.DockerCompose;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeComposeCSharpDockerTests
{
    [Fact]
    public async Task StandaloneCSharpAppHostPublishesDeploysAndRetainsAuthenticatedRedisData()
    {
        var executable = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_COMPOSE_CSHARP_APPHOST");
        var source = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_COMPOSE_SAMPLE_DIRECTORY");
        if (executable is null || source is null)
        {
            Assert.Skip("Supply the compiled C# Compose AppHost and shared TypeScript sample directory for real Docker acceptance.");
        }
        var work = Directory.CreateTempSubdirectory();
        var environment = "csharp-" + Guid.NewGuid().ToString("N");
        var image = "aspire-v2-compose-csharp-proof:" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        var project = DockerComposeGenerator.ProjectName(source, environment, "compose");
        var toolEnvironment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);
        toolEnvironment["ASPIRE_PARAMETER_CACHE_PASSWORD"] = password;
        toolEnvironment["ASPIRE_COMPOSE_EXPR_0001"] = password;
        toolEnvironment["COMPOSE_DISABLE_ENV_FILE"] = "1";
        string[] compose = ["compose", "--ansi", "never", "--env-file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            "-p", project, "-f", Path.Combine(work.FullName, "compose.yaml")];
        try
        {
            Assert.Equal(0, await AppHostAsync("publish", credential: null));
            var inputs = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(work.FullName, "deployment-inputs.json")))!;
            Assert.Equal(project, inputs["projectName"]!.GetValue<string>());
            Assert.NotEqual(0, await AppHostAsync("deploy", credential: null));
            Assert.Equal(0, await AppHostAsync("deploy", password));
            Assert.Equal("1", await IncrementAsync());
            Assert.Equal(0, await AppHostAsync("deploy", password));
            Assert.Equal("2", await IncrementAsync());

            var container = (await DockerAsync([.. compose, "ps", "--quiet", "cache"])).Stdout.Trim();
            Assert.Matches("^[a-f0-9]{64}$", container);
            var owner = await DockerAsync(["inspect", "--format", "{{index .Config.Labels \"com.docker.compose.project\"}}", container]);
            Assert.Equal(project, owner.Stdout.Trim());
            await DockerAsync(["rm", "--force", container]);
            Assert.Equal(0, await AppHostAsync("deploy", password));
            Assert.Equal("3", await IncrementAsync());
        }
        finally
        {
            try
            {
                if (File.Exists(Path.Combine(work.FullName, "compose.yaml")))
                {
                    await DockerAsync([.. compose, "down", "--volumes", "--remove-orphans"]);
                    var remaining = await DockerAsync(["ps", "--all", "--quiet", "--filter", "label=com.docker.compose.project=" + project]);
                    Assert.Equal("", remaining.Stdout.Trim());
                    var volumes = await DockerAsync(["volume", "ls", "--quiet", "--filter", "label=com.docker.compose.project=" + project]);
                    Assert.Equal("", volumes.Stdout.Trim());
                }
                var built = await DockerAsync(["image", "ls", "--quiet", "--filter", "reference=" + image]);
                if (built.Stdout.Trim().Length > 0)
                {
                    await DockerAsync(["image", "rm", image]);
                }
            }
            finally
            {
                work.Delete(true);
            }
        }

        async Task<int> AppHostAsync(string target, string? credential)
        {
            await using var host = await NativeClientTestHost.StartAsync();
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = source
            };
            start.ArgumentList.Add("--native-endpoint=" + host.Endpoint);
            start.ArgumentList.Add("--native-application-root=" + source);
            start.ArgumentList.Add("--step=" + target);
            start.ArgumentList.Add("--environment=" + environment);
            start.ArgumentList.Add("--output-path=" + work.FullName);
            start.ArgumentList.Add("--image=" + image);
            start.Environment["ASPIRE_NATIVE_RPC_TOKEN"] = host.Token;
            start.Environment.Remove("ASPIRE_PARAMETER_CACHE_PASSWORD");
            if (credential is not null)
            {
                start.Environment["ASPIRE_PARAMETER_CACHE_PASSWORD"] = credential;
            }
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
                await Task.WhenAll(stdout, stderr);
                if (credential is not null || target == "publish")
                {
                    Assert.True(process.ExitCode == 0, await stderr);
                }

                return process.ExitCode;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }

        async Task<string> IncrementAsync()
        {
            var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(work.FullName, "deployment-state.json")))!;
            var web = state["services"]!.AsArray().Single(service => service!["resource"]!.GetValue<string>() == "web")!;
            var url = web["endpoints"]![0]!["url"]!.GetValue<string>();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = JsonNode.Parse(await client.GetStringAsync(url, TestContext.Current.CancellationToken))!;

            return body["value"]!.GetValue<string>();
        }

        async Task<DockerCommandResult> DockerAsync(string[] arguments)
        {
            var result = await DockerCommand.RunAsync("docker", arguments,
                new(work.FullName, toolEnvironment, TimeSpan.FromSeconds(60), CancellationToken.None));
            Assert.True(result.ExitCode == 0, "Owned Compose acceptance operation failed.");

            return result;
        }
    }
}
