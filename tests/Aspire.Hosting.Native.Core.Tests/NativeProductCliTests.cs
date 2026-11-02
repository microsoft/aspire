// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using Aspire.Hosting.Native.Core.Tests.TestServices;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeProductCliTests
{
    [Fact]
    public async Task OptedInTypeScriptAppHostUsesPlatformRpcAndExistingCliBackchannels()
    {
        var cli = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_PRODUCT_CLI");
        var server = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_CSHARP_TEST_SERVER");
        if (cli is null || server is null)
        {
            Assert.Skip("The native validation lane builds the CLI and publishes the execution server.");
        }
        var directory = Directory.CreateTempSubdirectory("aspire-v2-cli-");
        var apphost = Path.Combine(directory.FullName, "apphost.mts");
        var proof = Path.Combine(directory.FullName, "proof.json");
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(apphost, """
                import { createBuilder } from './.aspire/modules/aspire.mjs';
                import { rename, writeFile } from 'node:fs/promises';
                import { existsSync } from 'node:fs';
                import { setTimeout } from 'node:timers/promises';

                const application = (await createBuilder()).build();
                try {
                    await application.start();
                    await writeFile(new URL('./proof.tmp', import.meta.url), JSON.stringify({ started: true }));
                    await rename(new URL('./proof.tmp', import.meta.url), new URL('./proof.json', import.meta.url));
                    while (!existsSync(new URL('./finish', import.meta.url))) {
                        await setTimeout(25);
                    }
                } finally {
                    await application.dispose();
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "aspire.config.json"),
                """{"appHostPath":"apphost.mts","features":{"appHostServerV2":true}}""");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "package.json"),
                Path.Combine(directory.FullName, "package.json"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tsconfig.apphost.json"),
                Path.Combine(directory.FullName, "tsconfig.apphost.json"));

            var start = StartInfo(["run"]);
            start.Environment["ASPIRE_CLI_NATIVE_APPHOST_SERVER"] = server;
            start.Environment.Remove("ASPIRE_LAYOUT_PATH");
            start.Environment.Remove("ASPIRE_REPO_ROOT");
            start.Environment.Remove("REMOTE_APP_HOST_SOCKET_PATH");
            start.Environment.Remove("ASPIRE_REMOTE_APPHOST_TOKEN");
            process = Process.Start(start) ?? throw new InvalidOperationException("The CLI did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            startup.CancelAfter(TimeSpan.FromMinutes(2));
            while (!File.Exists(proof))
            {
                if (process.HasExited)
                {
                    Assert.Fail($"CLI exited before the graph started: {await stdout}\n{await stderr}");
                }
                await Task.Delay(25, startup.Token);
            }
            var state = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(proof, startup.Token));
            Assert.True(state.GetProperty("started").GetBoolean());

            var description = await NativeIntegrationAppHostRunner.RunAsync(StartInfo(["describe", "--format", "Json"]), TimeProvider.System);
            using var resources = JsonDocument.Parse(description);
            Assert.Empty(resources.RootElement.GetProperty("resources").EnumerateArray());
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "finish"), "");
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, $"{await stdout}\n{await stderr}");
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                process.Dispose();
            }
            directory.Delete(recursive: true);
        }

        ProcessStartInfo StartInfo(string[] arguments)
        {
            var start = new ProcessStartInfo(cli)
            {
                WorkingDirectory = directory.FullName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments.Concat(["--apphost", apphost, "--non-interactive", "--nologo"]))
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment["ASPIRE_HOME"] = Path.Combine(directory.FullName, "home");

            return start;
        }
    }
}
